using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Media.Imaging;
using Avalonia.Platform.Storage;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using TitanEngine.Avalonia.Services;
using TitanEngine.Avalonia.Views;
using TitanEngine.Core;

namespace TitanEngine.Avalonia.ViewModels;

/// <summary>
/// Root VM — thay MainWindow.xaml.cs (~18k dòng) bằng: khu VMs + JobBuilder + EngineCore qua facade.
/// Batch loop / log / host / drag-drop mirror hành vi WPF. KHÔNG có logic engine mới.
/// </summary>
public partial class MainWindowViewModel : ObservableObject
{
    public SourceTrimViewModel SourceTrim { get; } = new();
    public FxStudioViewModel FxStudio { get; } = new();
    public SoundbarViewModel Soundbar { get; } = new();
    public SlowMotionViewModel SlowMotion { get; } = new();
    public UpscaleViewModel Upscale { get; } = new();
    public MotionEditorViewModel MotionEditor { get; } = new();
    public TextVibeViewModel TextVibe { get; } = new();
    public CaptionSyncViewModel CaptionSync { get; } = new();
    public AudioMixerViewModel AudioMixer { get; } = new();
    public WatermarkViewModel Watermark { get; } = new();
    public ColorGradingViewModel ColorGrading { get; } = new();
    public ExportViewModel Export { get; } = new();
    public QueueLogsViewModel QueueLogs { get; } = new();

    public ITitanEngineService Engine { get; } = new TitanEngineService();

    /// <summary>Window chủ — App gắn sau khi tạo MainWindow; cần cho StorageProvider + dialog.</summary>
    public Window? RootWindow { get; set; }

    private CancellationTokenSource? _batchCts;
    private bool _isBatchRunning;
    private readonly SemaphoreSlim _renderLock = new(1, 1);
    private LocalApiHost? _apiHost;

    /// <summary>Verify harness (--e2e): bỏ dialog port, mở host ngay trên port này.</summary>
    internal static int? E2EHostPort { get; set; }

    /// <summary>Verify harness (--e2e): đúng running-batch flag để mở thư mục output bằng xdg-open.</summary>
    internal static bool E2eMode { get; set; }

    [ObservableProperty] private string _logText = "";

    private readonly object _logLock = new();
    private const int MaxLogLines = 400;

    public MainWindowViewModel()
    {
        Engine.LogEmitted += m => AppendLog(m);

        SourceTrim.BrowseVideoRequested = () => _ = BrowseVideoAsync();
        SourceTrim.BrowseAudioRequested = () => _ = BrowseAudioAsync();
        SourceTrim.BrowseAudio2Requested = () => _ = BrowseAudio2Async();
        SourceTrim.EditAudioRequested = () => _ = OpenAudioEditorAsync();
        SourceTrim.RandomCombosRequested = () => _ = RandomCombosAsync();
        SourceTrim.AddToQueueRequested = () => _ = AddToQueueAsync();
        SourceTrim.SourcesChanged += OnSourcesChanged;

        Watermark.BrowseRequested = () => _ = BrowseWatermarkAsync();
        Watermark.OpenEditorRequested = () => _ = OpenWatermarkEditorAsync();

        Export.BrowseOutputRequested = () => _ = BrowseOutputFolderAsync();
        Export.OutputSizeChanged += () => Soundbar.RefreshCanvasAspect();
        Soundbar.ResolveOutputSize = ResolveSoundBarOutputSize;
        Soundbar.LogRequested += AppendLog;

        QueueLogs.StartBatchRequested = () => _ = StartBatchAsync();
        QueueLogs.DeleteJobRequested = job => DeleteJob(job);
        QueueLogs.ClearRequested = ClearQueue;
        QueueLogs.HostToggleRequested = () => _ = ToggleHostAsync();

        Engine.Initialize();
    }

    internal void AppendLog(string message)
    {
        lock (_logLock)
        {
            LogText = LogText.Length > 0 ? LogText + "\n" + message : message;
            int nl = 0, idx = LogText.Length - 1;
            // Giữ tối đa MaxLogLines dòng cuối (mirror txtLogs không xoá nhưng cần chặn phình vô hạn).
            for (int i = LogText.Length - 1; i >= 0 && nl <= MaxLogLines; i--)
                if (LogText[i] == '\n') { nl++; idx = i; }
            if (nl > MaxLogLines)
                LogText = LogText[(idx + 1)..];
        }
        OnPropertyChanged(nameof(LogText));
    }

    private void OnSourcesChanged()
    {
        Soundbar.LastProbedSource = SourceTrim.VideoPaths.FirstOrDefault(p => !string.IsNullOrWhiteSpace(p));
        _ = Soundbar.ProbeSourceResolutionAsync();
        Soundbar.RefreshCanvasAspect();
    }

    // ── MainWindow: TryGetSoundBarOutputSize ──
    private (int W, int H, bool Ok) ResolveSoundBarOutputSize()
    {
        string content = Export.Res.Label;
        if (string.IsNullOrWhiteSpace(content)) return (0, 0, false);

        if (content.Contains("Original", StringComparison.OrdinalIgnoreCase))
        {
            var (w, h) = Soundbar.ProbedSourceSize;
            if (w <= 0 || h <= 0) return (0, 0, false);
            return (w, h, true);
        }
        if (content.Contains("Custom", StringComparison.OrdinalIgnoreCase))
        {
            int w = (int)UiLogic.ParsePercent(Export.ResW, 0);
            int h = (int)UiLogic.ParsePercent(Export.ResH, 0);
            return (w > 0 && h > 0) ? (w, h, true) : (0, 0, false);
        }
        var m = System.Text.RegularExpressions.Regex.Match(content, @"(\d+)x(\d+)");
        if (!m.Success) return (0, 0, false);
        int pw = int.TryParse(m.Groups[1].Value, out pw) ? pw : 0;
        int ph = int.TryParse(m.Groups[2].Value, out ph) ? ph : 0;
        return (pw > 0 && ph > 0) ? (pw, ph, true) : (0, 0, false);
    }

    // ══════════ File pickers (IUserPrompt seam — StorageProvider) ══════════

    private IStorageProvider? Storage => RootWindow?.StorageProvider;

    private async Task<string[]?> PickFilesAsync(string title, params string[] patterns)
    {
        if (Storage == null) return null;
        var files = await Storage.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = title,
            AllowMultiple = true,
            FileTypeFilter = new[]
            {
                new FilePickerFileType("Media") { Patterns = patterns },
                new FilePickerFileType("All Files") { Patterns = new[] { "*" } },
            }
        });
        return files.Select(f => f.Path.LocalPath).ToArray();
    }

    // ── MainWindow: BtnBrowseVideo_Click ──
    private async Task BrowseVideoAsync()
    {
        var files = await PickFilesAsync("Chọn nguồn video/ảnh", UiLogic.SupportedMediaPatterns.Split(';'));
        if (files == null || files.Length == 0) return;
        SourceTrim.SetVideoPaths(new List<string>(files));
        int imageCount = files.Count(UiLogic.IsImageSourcePath);
        AppendLog($"[INPUT] {files.Length} source file(s) ({files.Length - imageCount} video, {imageCount} image)");
        OnSourcesChanged();
    }

    // ── MainWindow: BtnBrowseAudio_Click ──
    private async Task BrowseAudioAsync()
    {
        var files = await PickFilesAsync("Chọn Audio 1", UiLogic.AudioSourceFilterPattern.Split(';'));
        if (files == null || files.Length == 0) return;
        SourceTrim.SetAudioPaths(new List<string>(files));
        AppendLog($"[INPUT] {files.Length} audio(s)");
        if (files.Length > 1 && !SourceTrim.MergeAudios)
            AppendLog("[AUDIO-MERGE] Auto-selected (multiple audio files).");
    }

    // ── MainWindow: BtnBrowseAudio2_Click ──
    private async Task BrowseAudio2Async()
    {
        if (Storage == null) return;
        var files = await Storage.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = "Chọn Audio 2 (overlay)",
            AllowMultiple = false,
            FileTypeFilter = new[]
            {
                new FilePickerFileType("Audio") { Patterns = UiLogic.AudioSourceFilterPattern.Split(';') },
                new FilePickerFileType("All Files") { Patterns = new[] { "*" } },
            }
        });
        if (files.Count == 0) return;
        SourceTrim.SetSecondaryAudioPath(files[0].Path.LocalPath);
        AppendLog($"[INPUT] Audio 2 overlay: {Path.GetFileName(files[0].Path.LocalPath)}");
    }

    // ── MainWindow: BtnBrowseWatermark_Click ──
    private async Task BrowseWatermarkAsync()
    {
        if (Storage == null) return;
        var files = await Storage.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = "Chọn logo watermark",
            AllowMultiple = false,
            FileTypeFilter = new[]
            {
                new FilePickerFileType("Image") { Patterns = new[] { "*.png", "*.jpg", "*.jpeg" } },
                new FilePickerFileType("All Files") { Patterns = new[] { "*" } },
            }
        });
        if (files.Count == 0) return;
        string path = files[0].Path.LocalPath;
        double aspect = 1.0;
        try
        {
            using var stream = File.OpenRead(path);
            var bmp = new Bitmap(stream);
            if (bmp.PixelSize.Width > 0 && bmp.PixelSize.Height > 0)
                aspect = (double)bmp.PixelSize.Width / bmp.PixelSize.Height;
        }
        catch (Exception ex)
        {
            AppendLog($"[WATERMARK-ERROR] {ex.Message}");
            aspect = 1.0;
        }
        Watermark.SetPath(path, aspect);
        AppendLog($"[WATERMARK] {Path.GetFileName(path)}");
        AppendLog($"[WATERMARK] Aspect: {aspect:F3}:1");
    }

    // ── MainWindow: BtnBrowseOutput_Click ──
    private async Task BrowseOutputFolderAsync()
    {
        if (Storage == null) return;
        var folders = await Storage.OpenFolderPickerAsync(new FolderPickerOpenOptions
        {
            Title = "Select Output Folder",
            AllowMultiple = false
        });
        if (folders.Count == 0) return;
        string selected = folders[0].Path.LocalPath;
        Export.CustomOutputFolder = selected;
        Export.OutputDirDisplay = new DirectoryInfo(selected).Name;
        AppendLog($"[OUTPUT] {selected}");
    }

    // ══════════ Drag-drop (kèm fallback dán tay — Hyprland có thể không nhận) ══════════

    // ── MainWindow: OnWindowDrop ──
    public void IngestDroppedFiles(IEnumerable<string> files)
    {
        var vids = new List<string>();
        var auds = new List<string>();
        foreach (string f in files)
        {
            if (UiLogic.IsVideoSourcePath(f) || UiLogic.IsImageSourcePath(f)) vids.Add(f);
            else if (UiLogic.IsAudioSourcePath(f)) auds.Add(f);
        }

        if (vids.Count > 0)
        {
            SourceTrim.SetVideoPaths(vids);
            int imageCount = vids.Count(UiLogic.IsImageSourcePath);
            AppendLog($"[INPUT] {vids.Count} source file(s) ({vids.Count - imageCount} video, {imageCount} image)");
            OnSourcesChanged();
        }
        if (auds.Count > 0)
        {
            SourceTrim.SetAudioPaths(auds);
            AppendLog($"[INPUT] {auds.Count} audio(s)");
            if (auds.Count > 1 && !SourceTrim.MergeAudios)
                AppendLog("[AUDIO-MERGE] Auto-selected (multiple audio files).");
        }
    }

    // ══════════ ADD TO QUEUE — mirror BtnAddJob_Click ══════════

    [RelayCommand]
    private async Task AddToQueueAsync()
    {
        if (SourceTrim.VideoPaths.Count == 0)
        {
            UserPrompt.Alert("Error", "Select source media!");
            return;
        }

        bool mergeVideos = SourceTrim.MergeVideos && SourceTrim.VideoPaths.Count > 1;
        // Auto-merge template / Light Leak Burn (MainWindow:3673-3679).
        string templateCheck = LocalhostRequestMapper.NormalizeTemplateName(FxStudio.TemplateStyle.Label);
        string fxCheck = LocalhostRequestMapper.ResolveFxEffectName(FxStudio.FxType.Label);
        if ((templateCheck.Equals("Polaroid Scrapbook", StringComparison.OrdinalIgnoreCase)
             ? false
             : !templateCheck.Equals("None", StringComparison.OrdinalIgnoreCase) || fxCheck.Equals("Light Leak Burn", StringComparison.OrdinalIgnoreCase))
            && SourceTrim.VideoPaths.Count > 1)
        {
            mergeVideos = true;
            AppendLog(!templateCheck.Equals("None", StringComparison.OrdinalIgnoreCase)
                ? $"[TEMPLATE] Auto-merge enabled for template mode: {templateCheck}"
                : "[TRANSITION] Auto-merge enabled for Light Leak Burn transition");
        }

        bool mergeAudios = SourceTrim.MergeAudios && SourceTrim.AudioPaths.Count > 1;
        if (mergeVideos && SourceTrim.AudioPaths.Count > 1 && !mergeAudios)
        {
            mergeAudios = true;
            AppendLog("[AUDIO-MERGE] Auto-enabled because merge video mode has multiple audio tracks.");
        }

        List<string> queueSourceVideos = mergeVideos
            ? new List<string> { SourceTrim.VideoPaths[0] }
            : new List<string>(SourceTrim.VideoPaths);

        for (int i = 0; i < queueSourceVideos.Count; i++)
        {
            string vp = queueSourceVideos[i];
            string? ap = mergeAudios
                ? (SourceTrim.AudioPaths.Count > 0 ? SourceTrim.AudioPaths[0] : null)
                : (SourceTrim.AudioPaths.Count == 1 ? SourceTrim.AudioPaths[0]
                   : (SourceTrim.AudioPaths.Count > i ? SourceTrim.AudioPaths[i] : null));

            string outName = JobBuilder.BuildOutputName(vp, Export.OutputName, i, queueSourceVideos.Count, mergeVideos);

            RenderJob job = await JobBuilder.BuildJobAsync(
                this, vp, ap, outName, mergeVideos, mergeAudios,
                mergeVideos ? new List<string>(SourceTrim.VideoPaths) : null);

            QueueLogs.Jobs.Add(job);
            Engine.AddJob(job);
            if (job.MergeVideos && job.MergeInputPaths.Count > 1)
                AppendLog($"[QUEUED-MERGE] Job #{job.Id}: {job.MergeInputPaths.Count} video(s) -> {job.OutputName}");
            else
                AppendLog($"[QUEUED] Job #{job.Id}: {job.Name}");
            if (job.MergeAudio && job.MergeAudioPaths.Count > 1)
                AppendLog($"[QUEUED-AUDIO-MERGE] Job #{job.Id}: {job.MergeAudioPaths.Count} audio(s)");
            if (!string.IsNullOrWhiteSpace(job.SecondaryAudioPath))
                AppendLog($"[QUEUED-AUDIO-OVERLAY] Job #{job.Id}: Audio 2 will be mixed over Audio 1");
            AppendLog($"[SLOW-MOTION] Final output speed: {job.SlowMotionSpeed:F2}x");
        }

        SourceTrim.ClearSourceInputsAfterQueue();
        Export.OutputName = "";
    }

    private void DeleteJob(RenderJob job)
    {
        // ── MainWindow: BtnDeleteJob_Click ──
        if (QueueLogs.Jobs.Contains(job))
        {
            QueueLogs.Jobs.Remove(job);
            Engine.RemoveJob(job);
            for (int i = 0; i < QueueLogs.Jobs.Count; i++)
                QueueLogs.Jobs[i].Id = i + 1;
            Engine.RenumberJobs();
        }
    }

    private void ClearQueue()
    {
        foreach (var job in QueueLogs.Jobs.ToList())
            Engine.RemoveJob(job);
        QueueLogs.Jobs.Clear();
    }

    // ══════════ START BATCH — mirror BtnStartBatch_Click ══════════

    private async Task StartBatchAsync()
    {
        if (_isBatchRunning)
        {
            _batchCts?.Cancel();
            AppendLog("[STOP] Cancelling...");
            return;
        }

        if (!await _renderLock.WaitAsync(0))
        {
            UserPrompt.Alert("Host Busy", "API host is rendering a job. Please wait.");
            return;
        }

        var pending = QueueLogs.Jobs.Where(j => j.StatusDisplay == "WAITING" || j.StatusDisplay == "FAILED").ToList();
        if (pending.Count == 0)
        {
            _renderLock.Release();
            UserPrompt.Alert("Info", "Queue empty!");
            return;
        }

        _isBatchRunning = true;
        _batchCts = new CancellationTokenSource();
        QueueLogs.IsBatchRunning = true;
        AppendLog(">>> BATCH STARTED <<<");

        try
        {
            int total = pending.Count;
            int done = 0;

            foreach (var job in pending)
            {
                if (_batchCts.Token.IsCancellationRequested) break;

                job.Progress = 0;
                job.StatusDisplay = "PROCESSING...";
                job.StatusColor = "Yellow";
                AppendLog($"[START] Job #{job.Id}: {job.Name}");

                var progress = new Progress<RenderProgress>(p =>
                {
                    job.Progress = p.Percent;
                    job.StatusDisplay = p.StatusDisplay;
                    QueueLogs.GlobalProgress = p.Percent;
                });

                await Engine.RunJobAsync(job, new TitanEngineRunOptions
                {
                    CustomOutputFolder = Export.CustomOutputFolder,
                    Progress = progress
                }, _batchCts.Token);

                job.StatusDisplay = "DONE";
                job.StatusColor = "LimeGreen";
                job.Progress = 100;
                AppendLog($"[SUCCESS] Job #{job.Id} completed");
                done++;
                await Task.Delay(500);
            }

            UserPrompt.Alert("Done", $"Completed {done}/{total} jobs!");

            // ── Mở thư mục output (điểm 5 đã duyệt): xdg-open / explorer ──
            if (done > 0 && pending.Count > 0 && pending[0].ResultPath != null && !E2eMode)
            {
                try
                {
                    string? outPath = Path.GetDirectoryName(pending[0].ResultPath);
                    if (outPath != null && Directory.Exists(outPath))
                    {
                        System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
                        {
                            FileName = TitanPlatform.IsWindows ? "explorer.exe" : "xdg-open",
                            Arguments = outPath,
                            UseShellExecute = true
                        });
                        AppendLog($"[OUTPUT-FOLDER] Opened: {outPath}");
                    }
                }
                catch (Exception ex)
                {
                    AppendLog($"[OUTPUT-FOLDER] Could not open: {ex.Message}");
                }
            }
        }
        catch (OperationCanceledException)
        {
            AppendLog("[STOP] Batch cancelled");
        }
        catch (Exception ex)
        {
            AppendLog($"[FATAL] {ex.Message}");
        }
        finally
        {
            if (_renderLock.CurrentCount == 0)
                _renderLock.Release();
            _isBatchRunning = false;
            QueueLogs.IsBatchRunning = false;
            _batchCts?.Dispose();
            _batchCts = null;
        }
    }

    // ══════════ HOST MODE — mirror BtnHostMode_Click ══════════

    // internal (không private) để E2eRunner gọi await trực tiếp — cùng assembly, đúng đường của nút HOST.
    internal async Task ToggleHostAsync()
    {
        if (_isBatchRunning)
        {
            UserPrompt.Alert("Busy", "Cannot toggle host mode while batch is running.");
            return;
        }

        if (_apiHost != null && _apiHost.IsListening)
        {
            _apiHost.Stop();
            _apiHost = null;
            QueueLogs.IsHostRunning = false;
            AppendLog("[HOST] API server stopped");
            return;
        }

        string? portText = await ShowHostPortDialogAsync();
        if (portText == null) return;
        if (!int.TryParse(portText, out int port) || port is < 1 or > 65535)
        {
            UserPrompt.Alert("Host", "Invalid port.");
            return;
        }

        try
        {
            _apiHost = new LocalApiHost(this, _renderLock, $"http://127.0.0.1:{port}/");
            _apiHost.Start();
            QueueLogs.IsHostRunning = true;
            AppendLog($"[HOST] API server listening at http://127.0.0.1:{port}/");
        }
        catch (Exception ex)
        {
            UserPrompt.Alert("Host", $"Không mở được port {port}: {ex.Message}", error: true);
        }
    }

    private async Task<string?> ShowHostPortDialogAsync()
    {
        if (E2EHostPort is int e2ePort) return e2ePort.ToString(); // --e2e: bỏ dialog port
        if (RootWindow == null) return null;
        var dialog = new HostPortWindow();
        return await dialog.ShowDialog<string?>(RootWindow);
    }

    // ══════════ RANDOM COMBOS — mirror BtnRandomCombos_Click ══════════

    private async Task RandomCombosAsync()
    {
        if (SourceTrim.VideoPaths.Count == 0)
        {
            UserPrompt.Alert("Random", "Select source media first!");
            return;
        }
        if (SourceTrim.AudioPaths.Count == 0)
        {
            UserPrompt.Alert("Random", "Select at least 2 audio files to randomize!");
            return;
        }
        if (SourceTrim.VideoPaths.Count < 2 || SourceTrim.AudioPaths.Count < 2)
        {
            UserPrompt.Alert("Random", "Random needs multiple videos/images AND multiple audios (both > 1).");
            return;
        }
        if (SourceTrim.MergeVideos || SourceTrim.MergeAudios)
        {
            UserPrompt.Alert("Random", "Uncheck both Merge options to use Random combos.");
            return;
        }

        var videos = SourceTrim.VideoPaths.Where(p => !string.IsNullOrWhiteSpace(p) && File.Exists(p)).ToList();
        var audios = SourceTrim.AudioPaths.Where(p => !string.IsNullOrWhiteSpace(p) && File.Exists(p)).ToList();
        if (videos.Count < 2 || audios.Count < 2)
        {
            UserPrompt.Alert("Random", "Some selected files no longer exist. Need at least 2 videos and 2 audios.");
            return;
        }

        long totalCombos = (long)videos.Count * (long)audios.Count;
        if (totalCombos > 100)
        {
            if (!UserPrompt.Confirm("Random", $"This will create {totalCombos} queue jobs ({videos.Count} x {audios.Count}). Continue?"))
                return;
        }

        var pairs = new List<(string Video, string Audio)>();
        foreach (var v in videos)
            foreach (var a in audios)
                pairs.Add((v, a));
        var rng = new Random();
        for (int i = pairs.Count - 1; i > 0; i--)
        {
            int j = rng.Next(i + 1);
            (pairs[i], pairs[j]) = (pairs[j], pairs[i]);
        }

        string outputBase = Export.OutputName.Trim();
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

            try
            {
                RenderJob job = await JobBuilder.BuildJobAsync(
                    this, pairs[i].Video, pairs[i].Audio, outName,
                    mergeVideos: false, mergeAudios: false, mergeInputPaths: null);
                QueueLogs.Jobs.Add(job);
                Engine.AddJob(job);
                created++;
            }
            catch (Exception ex)
            {
                AppendLog($"[RANDOM-ERROR] {pairs[i].Video}: {ex.Message}");
            }
        }

        AppendLog($"[RANDOM] Created {created}/{pairs.Count} queue jobs ({videos.Count} video x {audios.Count} audio, shuffled, settings preserved).");
        SourceTrim.ClearSourceInputsAfterQueue();
        Export.OutputName = "";
    }

    // ── MainWindow: SanitizeRandomNamePart ──
    private static string SanitizeRandomNamePart(string? input, string fallback)
    {
        if (string.IsNullOrWhiteSpace(input)) return fallback;
        string sanitized = new string(input.Select(c => char.IsLetterOrDigit(c) || c is '-' or '_' ? c : '_').ToArray()).Trim('_');
        return string.IsNullOrWhiteSpace(sanitized) ? fallback : sanitized;
    }

    // ══════════ WATERMARK EDITOR — điểm 1 đã duyệt ══════════

    // ── MainWindow: BtnOpenWatermarkEditor_Click (frame t=0.0 — bản gốc KHÔNG scrub) ──
    private async Task OpenWatermarkEditorAsync()
    {
        if (SourceTrim.VideoPaths.Count == 0)
        {
            UserPrompt.Alert("Error", "Select video first!");
            return;
        }
        if (string.IsNullOrWhiteSpace(Watermark.WatermarkPath) || !File.Exists(Watermark.WatermarkPath))
        {
            UserPrompt.Alert("Error", "Select watermark first!");
            return;
        }

        string previewVideo = SourceTrim.VideoPaths[0];
        AppendLog("[WATERMARK-EDITOR] Opened");
        try
        {
            var editor = new WatermarkEditorWindow(previewVideo, Watermark.WatermarkPath,
                Watermark.Scale, Watermark.Rotation, Watermark.Opacity, Watermark.XPercent, Watermark.YPercent);
            if (RootWindow != null)
            {
                var (scale, rotation, opacity, x, y, saved) = await editor.ShowResultAsync(RootWindow);
                if (saved)
                {
                    Watermark.Scale = scale;
                    Watermark.Rotation = rotation;
                    Watermark.Opacity = opacity;
                    Watermark.XPercent = x;
                    Watermark.YPercent = y;
                }
            }
        }
        catch (Exception ex)
        {
            AppendLog($"[WATERMARK-EDITOR] Error: {ex.Message}");
        }
    }

    // ══════════ AUDIO EDITOR — điểm 2 đã duyệt (ffplay) ══════════

    // ── MainWindow: BtnOpenAudioEditor_Click ──
    private async Task OpenAudioEditorAsync()
    {
        if (SourceTrim.AudioPaths.Count == 0)
        {
            UserPrompt.Alert("Error", "Select audio first!");
            return;
        }

        string previewAudioPath = SourceTrim.AudioPaths[0];
        string? tempConcatListPath = null;
        string? tempMergedAudioPath = null;
        bool cleanupMergedPreview = false;

        try
        {
            if (SourceTrim.AudioPaths.Count > 1 && SourceTrim.MergeAudios)
            {
                string tempDir = Path.Combine(Path.GetTempPath(), "TitanEngine_AudioPreview");
                Directory.CreateDirectory(tempDir);
                string previewTag = $"preview_{Guid.NewGuid():N}";
                (tempConcatListPath, tempMergedAudioPath) = await EngineCore.MergeAudiosToTempTrackAsync(
                    SourceTrim.AudioPaths, tempDir, previewTag, CancellationToken.None, AppendLog);
                if (!string.IsNullOrWhiteSpace(tempMergedAudioPath) && File.Exists(tempMergedAudioPath))
                {
                    previewAudioPath = tempMergedAudioPath;
                    cleanupMergedPreview = true;
                    AppendLog($"[AUDIO-EDITOR] Preview merged track ready: {Path.GetFileName(previewAudioPath)}");
                }
            }
            else if (SourceTrim.AudioPaths.Count > 1)
            {
                AppendLog("[AUDIO-EDITOR] Multiple audio files selected. Opening editor for the first track preview.");
            }

            var meta = await EngineCore.AnalyzeMediaAsync(previewAudioPath);
            double durationSec = meta.Duration > 0.0 ? meta.Duration : 0.0;

            var editor = new AudioEditorWindow(previewAudioPath, durationSec, new List<AudioEditSegment>(SourceTrim.AudioEditSegments));
            if (RootWindow != null)
            {
                List<AudioEditSegment>? result = await editor.ShowResultAsync(RootWindow);
                if (result != null)
                {
                    SourceTrim.SetAudioEditSegments(AudioEditHelpers.NormalizeSegments(result, durationSec));
                    AppendLog(SourceTrim.AudioEditSegments.Count > 0
                        ? $"[AUDIO-EDITOR] Saved {SourceTrim.AudioEditSegments.Count} cut segment(s)"
                        : "[AUDIO-EDITOR] Saved without cuts");
                }
            }
        }
        catch (Exception ex)
        {
            AppendLog($"[AUDIO-EDITOR-ERROR] {ex.Message}");
            UserPrompt.Alert("Audio Editor", $"Audio editor failed: {ex.Message}", error: true);
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

    /// <summary>Gọi từ code-behind khi ô đường dẫn bị dán tay (fallback Hyprland).</summary>
    public void IngestPastedPath(string text, string kind)
    {
        switch (kind)
        {
            case "video": SourceTrim.IngestPastedVideoPath(text); break;
            case "audio1": SourceTrim.IngestPastedAudioPath(text, secondary: false); break;
            case "audio2": SourceTrim.IngestPastedAudioPath(text, secondary: true); break;
        }
    }
}

/// <summary>
/// Khu QUEUE + LOGS — mirror lstQueue/pbGlobalProgress/txtLogs/btnClear/btnStart/btnHostMode.
/// </summary>
public partial class QueueLogsViewModel : ObservableObject
{
    public ObservableCollection<RenderJob> Jobs { get; } = new();

    public bool HasJobs => Jobs.Count > 0;

    public QueueLogsViewModel()
    {
        Jobs.CollectionChanged += (_, _) => OnPropertyChanged(nameof(HasJobs));
    }

    public Action? StartBatchRequested { get; set; }
    public Action<RenderJob>? DeleteJobRequested { get; set; }
    public Action? ClearRequested { get; set; }
    public Action? HostToggleRequested { get; set; }

    [ObservableProperty] private double _globalProgress;
    [ObservableProperty] private bool _isBatchRunning;
    [ObservableProperty] private bool _isHostRunning;

    public string StartButtonText => IsBatchRunning ? "STOP RENDER" : "START BATCH";

    public string HostButtonText => IsHostRunning ? "HOST: ON" : "HOST: OFF";

    partial void OnIsBatchRunningChanged(bool value)
    {
        OnPropertyChanged(nameof(StartButtonText));
    }

    partial void OnIsHostRunningChanged(bool value) => OnPropertyChanged(nameof(HostButtonText));

    [RelayCommand] private void StartBatch() => StartBatchRequested?.Invoke();
    [RelayCommand] private void Clear() => ClearRequested?.Invoke();
    [RelayCommand] private void ToggleHost() => HostToggleRequested?.Invoke();

    [RelayCommand]
    private void DeleteJob(object? param)
    {
        if (param is RenderJob job) DeleteJobRequested?.Invoke(job);
    }
}
