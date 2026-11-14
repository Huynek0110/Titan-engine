using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace TitanEngine.Core;

/// <summary>
/// Giai đoạn 5 — server HTTP host dùng chung (tách từ TitanEngine.Cli/HostCommand.cs nguyên văn,
/// statics → instance; hành vi KHÔNG đổi). CLI `host` và HOST: OFF/ON của Avalonia cùng đi qua đây.
/// Mirror ngữ nghĩa handler của MainWindow: cùng LocalhostRequestMapper, cùng
/// ApiRenderJobState/LocalhostRenderResponse, cùng status codes (400 JSON lỗi, 404 jobId,
/// 405 method, 409 batch đang chạy, 429 host bận).
/// Endpoint: POST /api/render | /api/render/async | /api/render/wait |
///           POST /api/queue/add | POST /api/queue/start | GET /api/render/status?jobId=
/// (Gap so MainWindow đã ghi PORT-PROGRESS: /api/soundbar/presets chưa có — đầu vào Giai đoạn 4.)
/// </summary>
public sealed class LocalHostServer
{
    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        PropertyNameCaseInsensitive = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = true,
    };

    private readonly SemaphoreSlim _renderLock = new(1, 1);
    private readonly object _stateLock = new();
    private readonly Dictionary<string, ApiRenderJobState> _states = new(StringComparer.OrdinalIgnoreCase);
    private readonly List<RenderJob> _queue = new();
    private readonly ITitanEngineService _engine;
    private readonly Action<string>? _log;
    private readonly Action<string>? _logError;
    private readonly SemaphoreSlim? _externalRenderLock;

    private System.Net.HttpListener? _listener;
    private CancellationTokenSource? _cts;
    private Task? _loopTask;
    private bool _batchRunning;
    private string _prefix = "http://127.0.0.1:5555/";

    public bool IsListening => _listener?.IsListening ?? false;

    /// <param name="engine">Facade engine (CLI tự tạo; Avalonia truyền instance của UI).</param>
    /// <param name="prefix">http://host:port/</param>
    /// <param name="externalRenderLock">
    /// Khoá render chung với UI batch (WPF truyền _localApiRenderLock vào host của nó —
    /// mirror BtnStartBatch_Click:4455). Null = server tự giữ khoá riêng.
    /// </param>
    public LocalHostServer(ITitanEngineService engine, string prefix,
        SemaphoreSlim? externalRenderLock = null, Action<string>? log = null, Action<string>? logError = null)
    {
        _engine = engine;
        _prefix = prefix;
        _externalRenderLock = externalRenderLock;
        _log = log;
        _logError = logError;
    }

    public void Start()
    {
        if (IsListening) return;

        _listener = new System.Net.HttpListener();
        _listener.Prefixes.Add(_prefix);
        _listener.Start();

        _cts = new CancellationTokenSource();
        _loopTask = Task.Run(() => RunLoopAsync(_cts.Token));

        _log?.Invoke($"[HOST] Host mode ON - {_prefix.TrimEnd('/')}/api/render");
        _log?.Invoke($"[HOST] Queue add endpoint - {_prefix.TrimEnd('/')}/api/queue/add");
    }

    public void Stop()
    {
        try { _cts?.Cancel(); } catch { }
        try { _listener?.Stop(); } catch { }
        _log?.Invoke("[HOST] stopped");
    }

    private async Task RunLoopAsync(CancellationToken token)
    {
        var listener = _listener!;
        while (!token.IsCancellationRequested)
        {
            System.Net.HttpListenerContext context;
            try
            {
                context = await listener.GetContextAsync();
            }
            catch (Exception) when (token.IsCancellationRequested)
            {
                break;
            }
            _ = Task.Run(() => HandleAsync(context, token), token);
        }
    }

    private async Task HandleAsync(System.Net.HttpListenerContext context, CancellationToken token)
    {
        try
        {
            string path = context.Request.Url?.AbsolutePath ?? "/";
            if (path.Length > 1) path = path.TrimEnd('/');
            string method = context.Request.HttpMethod.ToUpperInvariant();

            if (method == "OPTIONS")
            {
                await RespondAsync(context, 200, new LocalhostRenderResponse { Success = true, Message = "OK" });
                return;
            }

            if (path.Equals("/api/render/status", StringComparison.OrdinalIgnoreCase))
            {
                if (method != "GET") { await MethodNotAllowedAsync(context, "Use GET /api/render/status"); return; }
                await StatusAsync(context, context.Request.QueryString["jobId"] ?? string.Empty);
                return;
            }

            if (path.Equals("/api/queue/add", StringComparison.OrdinalIgnoreCase))
            {
                if (method != "POST") { await MethodNotAllowedAsync(context, "Use POST /api/queue/add"); return; }
                await QueueAddAsync(context, token);
                return;
            }

            if (path.Equals("/api/queue/start", StringComparison.OrdinalIgnoreCase))
            {
                if (method != "POST") { await MethodNotAllowedAsync(context, "Use POST /api/queue/start"); return; }
                await QueueStartAsync(context, token);
                return;
            }

            if (path.Equals("/api/render", StringComparison.OrdinalIgnoreCase) ||
                path.Equals("/api/render/async", StringComparison.OrdinalIgnoreCase) ||
                path.Equals("/api/render/wait", StringComparison.OrdinalIgnoreCase))
            {
                if (method != "POST") { await MethodNotAllowedAsync(context, "Use POST /api/render, POST /api/render/async, or POST /api/render/wait"); return; }
                bool forceWait = path.Equals("/api/render/wait", StringComparison.OrdinalIgnoreCase);
                bool forceAsync = path.Equals("/api/render/async", StringComparison.OrdinalIgnoreCase);
                await RenderAsync(context, forceWait, forceAsync, queueAdd: false, token);
                return;
            }

            await RespondAsync(context, 404, new LocalhostRenderResponse
            {
                Success = false,
                Message = "Endpoint not found. Use POST /api/queue/add, POST /api/queue/start, POST /api/render, or GET /api/render/status"
            });
        }
        catch (Exception ex)
        {
            _logError?.Invoke($"[HOST] handler error: {ex}");
            try { await RespondAsync(context, 500, new LocalhostRenderResponse { Success = false, Message = ex.Message }); }
            catch { /* client đã ngắt */ }
        }
    }

    private async Task MethodNotAllowedAsync(System.Net.HttpListenerContext context, string message)
        => await RespondAsync(context, 405, new LocalhostRenderResponse { Success = false, Message = message });

    private async Task<LocalhostRenderRequest?> ReadRequestAsync(System.Net.HttpListenerContext context)
    {
        using var reader = new StreamReader(context.Request.InputStream, context.Request.ContentEncoding ?? Encoding.UTF8);
        string body = await reader.ReadToEndAsync();
        if (string.IsNullOrWhiteSpace(body)) return null;
        return JsonSerializer.Deserialize<LocalhostRenderRequest>(body, JsonOpts);
    }

    // ── GET /api/render/status ──
    private async Task StatusAsync(System.Net.HttpListenerContext context, string statusJobId)
    {
        statusJobId = statusJobId.Trim();
        if (statusJobId.Length == 0)
        {
            await RespondAsync(context, 400, new LocalhostRenderResponse { Success = false, Message = "jobId is required" });
            return;
        }

        ApiRenderJobState? state;
        lock (_stateLock) _states.TryGetValue(statusJobId, out state);
        if (state == null)
        {
            await RespondAsync(context, 404, new LocalhostRenderResponse
            {
                Success = false,
                QueueId = int.TryParse(statusJobId, out int qid) ? qid : null,
                Message = "jobId not found",
                JobId = statusJobId
            });
            return;
        }

        long? outputFileSize = File.Exists(state.FinalOutput) ? new FileInfo(state.FinalOutput).Length : null;
        _log?.Invoke($"[HOST] status {statusJobId}: {state.Status} {state.Progress:F1}% size={outputFileSize}");
        await RespondAsync(context, 200, StateToResponse(state));
    }

    private LocalhostRenderResponse StateToResponse(ApiRenderJobState state) => new()
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
        InputFiles = state.InputFiles,
        InputCount = state.InputFiles?.Count,
        MergeVideos = state.MergeVideos,
        AudioFile = state.AudioFile,
        AudioFile2 = state.AudioFile2,
        AudioFiles = state.AudioFiles,
        MergeAudio = state.MergeAudio,
        AudioTrimStart = state.AudioTrimStart,
        AudioTrimEnd = state.AudioTrimEnd,
        AudioTrimDuration = state.AudioTrimDuration,
        AudioSegments = state.AudioSegments,
        OutputVideo = state.OutputVideo,
        TemplateName = state.TemplateName,
        OverlayEffect = state.OverlayEffect,
    };

    // Mirror BuildQueueJobInfo của MainWindow: info trả kèm response, state-less fallback về job.
    private LocalhostQueueJobInfo BuildJobInfo(RenderJob job)
    {
        ApiRenderJobState? state;
        lock (_stateLock) _states.TryGetValue(job.Guid, out state);
        string outputVideo = state?.OutputVideo ?? state?.RequestedOutput ?? job.SourcePath + ".out.mp4";
        bool done = state?.Done ?? (job.StatusDisplay?.Contains("DONE", StringComparison.OrdinalIgnoreCase) ?? false);
        bool success = state?.Success ?? done;
        string status = state?.Status ?? ResolveApiStatusFromJob(job);

        return new LocalhostQueueJobInfo
        {
            QueueId = job.Id,
            JobId = job.Guid,
            VideoId = job.Guid,
            Status = status,
            Done = done,
            JobSuccess = success,
            Progress = state?.Progress ?? job.Progress,
            StatusUrl = $"{_prefix.TrimEnd('/')}/api/render/status?jobId={job.Guid}",
            InputFile = state?.InputFile ?? job.SourcePath,
            InputFiles = state?.InputFiles ?? (job.MergeVideos ? new List<string>(job.MergeInputPaths) : new List<string> { job.SourcePath }),
            InputCount = state?.InputFiles?.Count ?? (job.MergeVideos ? job.MergeInputPaths.Count : 1),
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
                .Select(seg => new AudioEditSegment { StartSeconds = seg.StartSeconds, EndSeconds = seg.EndSeconds })
                .ToList()),
            OutputVideo = outputVideo,
            TemplateName = state?.TemplateName ?? job.TemplateName,
            OverlayEffect = state?.OverlayEffect ?? job.OverlayEffect,
            EnableSnowOverlay1 = (state?.EnableSnowOverlay1 ?? false) || job.EnableSnowOverlay1,
            EnableSnowOverlay2 = (state?.EnableSnowOverlay2 ?? false) || job.EnableSnowOverlay2,
            EnableSnowOverlay3 = (state?.EnableSnowOverlay3 ?? false) || job.EnableSnowOverlay3,
            EnableCrossTransitions = (state?.EnableCrossTransitions ?? false) || job.EnableCrossTransitions,
            CrossTransitionType = (!string.IsNullOrWhiteSpace(state?.CrossTransitionType) ? state?.CrossTransitionType : null) ?? job.CrossTransitionType,
            CrossTransitionDuration = ((state?.CrossTransitionDuration ?? 0) > 0.01 ? state?.CrossTransitionDuration : null) ?? job.CrossTransitionDuration,
            EnablePolaroidScrapbook = state?.EnablePolaroidScrapbook ?? job.EnablePolaroidScrapbook,
            EnableRgbPolaroidScrapbook = state?.EnableRgbPolaroidScrapbook ?? job.EnableRgbPolaroidScrapbook,
            EnableDashedPolaroidScrapbook = state?.EnableDashedPolaroidScrapbook ?? job.EnableDashedPolaroidScrapbook,
            EnableBrandLogo = state?.EnableBrandLogo ?? job.EnableBrandLogo,
            BrandLogoPosition = state?.BrandLogoPosition ?? job.BrandLogoPosition,
            SnowfallPreset = state?.SnowfallPreset ?? job.SnowfallPreset,
            SnowfallMode = state?.SnowfallMode ?? job.SnowfallMode,
            SnowfallOpacity = ((state?.SnowfallOpacity ?? 0) > 0.0 ? state?.SnowfallOpacity : null) ?? job.SnowfallOpacity,
            SnowOverlay1Opacity = ((state?.SnowOverlay1Opacity ?? 0) > 0.0 ? state?.SnowOverlay1Opacity : null) ?? job.SnowOverlay1Opacity,
            SnowOverlay2Opacity = ((state?.SnowOverlay2Opacity ?? 0) > 0.0 ? state?.SnowOverlay2Opacity : null) ?? job.SnowOverlay2Opacity,
            SnowOverlay3Opacity = ((state?.SnowOverlay3Opacity ?? 0) > 0.0 ? state?.SnowOverlay3Opacity : null) ?? job.SnowOverlay3Opacity,
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
            OutputWidth = job.TargetWidth > 0 ? job.TargetWidth : (job.SourceWidth > 0 ? job.SourceWidth : null),
            OutputHeight = job.TargetHeight > 0 ? job.TargetHeight : (job.SourceHeight > 0 ? job.SourceHeight : null),
            OutputFramerate = job.Framerate,
            EnableFade = job.EnableFade,
            FadeInSeconds = job.FadeInSeconds,
            FadeOutSeconds = job.FadeOutSeconds,
        };
    }

    private string ResolveApiStatusFromJob(RenderJob job)
    {
        string status = job.StatusDisplay ?? string.Empty;
        if (status.Contains("DONE", StringComparison.OrdinalIgnoreCase)) return "completed";
        if (status.Contains("FAILED", StringComparison.OrdinalIgnoreCase)) return "failed";
        if (status.Contains("PROCESS", StringComparison.OrdinalIgnoreCase) || status.EndsWith("%", StringComparison.OrdinalIgnoreCase)) return "processing";
        return "queued";
    }

    private List<string>? BuildAudioFilesForJob(RenderJob job)
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

    // ── POST /api/queue/add ── (cùng đường map + initial state như MainWindow)
    private async Task QueueAddAsync(System.Net.HttpListenerContext context, CancellationToken token)
    {
        LocalhostRenderRequest? request;
        try { request = await ReadRequestAsync(context); }
        catch (JsonException) { request = null; }
        if (request == null)
        {
            await RespondAsync(context, 400, new LocalhostRenderResponse { Success = false, Message = "Invalid JSON body" });
            return;
        }

        var mapped = await MapOrErrorAsync(context, request);
        if (mapped == null) return;
        RenderJob job = mapped.Job;

        int id;
        lock (_stateLock)
        {
            job.Id = _queue.Count + 1;
            _queue.Add(job);
            id = job.Id;
        }

        string inputDesc = job.MergeVideos
            ? $"{job.MergeInputPaths.Count} video(s) [MERGE]"
            : Path.GetFileName(mapped.InputPath);
        _log?.Invoke($"[API] queueAdd=true ({inputDesc} -> {mapped.OutputPath})");

        SetInitialState(job, mapped);
        string jobId = job.Guid;
        ApiRenderJobState state = GetState(jobId);
        await RespondAsync(context, 200, new LocalhostRenderResponse
        {
            Success = true,
            QueueId = id,
            Message = job.MergeVideos
                ? "Added merged job to queue. Call POST /api/queue/start to render"
                : "Added to queue. Call POST /api/queue/start to render",
            JobId = jobId,
            Status = state.Status,
            Done = state.Done,
            JobSuccess = state.Success,
            Progress = state.Progress,
            Jobs = new List<LocalhostQueueJobInfo> { BuildJobInfo(job) },
        });
    }

    // ── POST /api/queue/start ──
    private async Task QueueStartAsync(System.Net.HttpListenerContext context, CancellationToken token)
    {
        if (_batchRunning)
        {
            await RespondAsync(context, 409, new LocalhostRenderResponse { Success = false, Message = "Queue is already running" });
            return;
        }

        List<RenderJob> pending;
        int totalJobsInQueue;
        lock (_stateLock)
        {
            totalJobsInQueue = _queue.Count;
            pending = _queue.Where(j => j.StatusDisplay == "WAITING" || j.StatusDisplay == "FAILED" || j.StatusDisplay == "FAILED (API)").ToList();
        }

        if (pending.Count == 0)
        {
            await RespondAsync(context, 200, new LocalhostRenderResponse
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

        if (!await ResolveLock().WaitAsync(0, token))
        {
            await RespondAsync(context, 429, new LocalhostRenderResponse { Success = false, Message = "Host is busy rendering another request" });
            return;
        }

        _batchRunning = true;
        foreach (RenderJob pendingJob in pending)
            UpdateState(pendingJob.Guid, s => { s.Status = "queued"; s.Done = false; s.Success = false; s.Message = "Queued. Waiting for queue renderer"; });

        _ = Task.Run(() => RunQueueLoopAsync(pending, token), token);

        await RespondAsync(context, 200, new LocalhostRenderResponse
        {
            Success = true,
            Message = "Queue rendering started",
            Status = "started",
            Done = false,
            TotalJobs = totalJobsInQueue,
            StartedJobs = pending.Count,
        });
    }

    private SemaphoreSlim ResolveLock() => _externalRenderLock ?? _renderLock;

    private async Task RunQueueLoopAsync(List<RenderJob> pending, CancellationToken token)
    {
        try
        {
            foreach (RenderJob job in pending)
            {
                string requested = GetState(job.Guid).RequestedOutput ?? string.Empty;
                await ExecuteJobAsync(job, requested, token);
            }
        }
        finally
        {
            _batchRunning = false;
            ResolveLock().Release();
            _log?.Invoke("[HOST] queue finished");
        }
    }

    // ── POST /api/render | /api/render/async | /api/render/wait ──
    private async Task RenderAsync(System.Net.HttpListenerContext context, bool forceWaitByPath, bool forceAsyncByPath, bool queueAdd, CancellationToken token)
    {
        if (_batchRunning)
        {
            await RespondAsync(context, 409, new LocalhostRenderResponse { Success = false, Message = "UI batch is running" });
            return;
        }

        LocalhostRenderRequest? request;
        try { request = await ReadRequestAsync(context); }
        catch (JsonException) { request = null; }
        if (request == null)
        {
            await RespondAsync(context, 400, new LocalhostRenderResponse { Success = false, Message = "Invalid JSON body" });
            return;
        }

        bool? waitFromQuery = ParseApiBoolQuery(
            context.Request.QueryString["waitForCompletion"] ??
            context.Request.QueryString["wait"] ??
            context.Request.QueryString["sync"] ??
            context.Request.QueryString["blocking"]);
        bool? waitFromBody = request.WaitForCompletion ??
            LocalhostRequestMapper.GetFeatureBool(request, "waitForCompletion", "wait", "sync", "blocking");

        // Default BAS flow: POST /api/render trả job info ngay; wait=true hoặc /api/render/wait mới đợi.
        bool waitForCompletion = forceWaitByPath && !queueAdd;
        if (forceAsyncByPath) waitForCompletion = false;
        else if (!queueAdd && waitFromBody.HasValue) waitForCompletion = waitFromBody.Value;
        else if (!queueAdd && waitFromQuery.HasValue) waitForCompletion = waitFromQuery.Value;

        _log?.Invoke($"[API] waitForCompletion={(waitForCompletion ? "true" : "false")}");

        if (!await ResolveLock().WaitAsync(0, token))
        {
            await RespondAsync(context, 429, new LocalhostRenderResponse { Success = false, Message = "Host is busy rendering another request" });
            return;
        }

        var mapped = await MapOrErrorAsync(context, request);
        if (mapped == null) { ResolveLock().Release(); return; }
        RenderJob job = mapped.Job;

        lock (_stateLock)
        {
            job.Id = _queue.Count + 1;
            _queue.Add(job);
        }
        _log?.Invoke($"[API] Incoming render: {(job.MergeVideos ? $"{job.MergeInputPaths.Count} video(s) [MERGE]" : Path.GetFileName(mapped.InputPath))} -> {mapped.OutputPath}");
        _log?.Invoke($"[API] Feature: colorgrading={mapped.ColorFilter}, fx={mapped.FxEffect}, overlay={mapped.OverlayEffect}, template={mapped.TemplateName}, crop={(mapped.CropEnabled ? "on" : "off")}, upscale={mapped.UpscaleMode}");

        SetInitialState(job, mapped);
        string jobId = job.Guid;

        if (!waitForCompletion)
        {
            // Async: giữ lock cho task nền, trả job info ngay (giữ semantics handler gốc).
            _ = Task.Run(async () =>
            {
                try
                {
                    await ExecuteJobAsync(job, mapped.OutputPath, token);
                }
                finally { ResolveLock().Release(); }
            }, token);

            ApiRenderJobState st = GetState(jobId);
            await RespondAsync(context, 200, new LocalhostRenderResponse
            {
                Success = true,
                QueueId = job.Id,
                JobId = jobId,
                Status = st.Status,
                Done = false,
                JobSuccess = false,
                Progress = 0,
                RequestedOutput = mapped.OutputPath,
                Message = "Render started. Poll /api/render/status?jobId=" + jobId,
                StatusUrl = $"{_prefix.TrimEnd('/')}/api/render/status?jobId={jobId}",
            });
            return;
        }

        // Wait mode: render đến xong rồi trả kết quả (lock giữ ở caller).
        try
        {
            (RenderRunResult result, string engineOutput, string finalOutput) = await ExecuteJobAsync(job, mapped.OutputPath, token);
            ApiRenderJobState st = GetState(jobId);
            await RespondAsync(context, 200, new LocalhostRenderResponse
            {
                Success = true,
                QueueId = job.Id,
                JobId = jobId,
                Status = st.Status,
                Done = true,
                JobSuccess = result.Success,
                Progress = job.Progress,
                RequestedOutput = mapped.OutputPath,
                EngineOutput = engineOutput,
                FinalOutput = result.Success ? finalOutput : null,
                Message = result.StatusDisplay,
                StatusUrl = $"{_prefix.TrimEnd('/')}/api/render/status?jobId={jobId}",
            });
        }
        finally
        {
            ResolveLock().Release();
        }
    }

    private async Task<LocalhostRequestMapping?> MapOrErrorAsync(System.Net.HttpListenerContext context, LocalhostRenderRequest request)
    {
        try
        {
            return await LocalhostRequestMapper.MapAsync(request, m => _log?.Invoke(m));
        }
        catch (LocalhostRequestException ex)
        {
            await RespondAsync(context, ex.StatusCode, new LocalhostRenderResponse { Success = false, Message = ex.Message });
            return null;
        }
    }

    // Mirror RunApiRenderCoreAsync: chạy engine với outputDir của request, sau đó MOVE
    // engine output tới đúng path yêu cầu (fallback copy+delete), rồi cập nhật state
    // completed/failed — giữ semantics cho BAS client poll /api/render/status.
    private async Task<(RenderRunResult Result, string EngineOutput, string FinalOutput)> ExecuteJobAsync(
        RenderJob job, string requestedOutput, CancellationToken token)
    {
        UpdateState(job.Guid, s => { s.Status = "processing"; s.Message = "Processing 0.0%"; s.Done = false; });
        var options = new TitanEngineRunOptions
        {
            Progress = new Progress<RenderProgress>(p => UpdateState(job.Guid, s =>
            {
                s.Progress = p.Percent;
                s.Message = $"Processing {p.Percent:F1}%";
            })),
        };
        RenderRunResult result = await _engine.RunJobAsync(job, options, token);

        string engineOutput = result.OutputPath ?? string.Empty;
        string finalOutput = engineOutput;
        if (result.Success && engineOutput.Length > 0 && requestedOutput.Length > 0 &&
            !Path.GetFullPath(engineOutput).Equals(Path.GetFullPath(requestedOutput),
                TitanPlatform.IsWindows ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal))
        {
            try
            {
                string? targetDir = Path.GetDirectoryName(requestedOutput);
                if (!string.IsNullOrWhiteSpace(targetDir))
                    Directory.CreateDirectory(targetDir);
                if (File.Exists(requestedOutput))
                    File.Delete(requestedOutput);
                File.Move(engineOutput, requestedOutput);
                _log?.Invoke($"[API] Moved output to requested path: {requestedOutput}");
            }
            catch (Exception moveEx)
            {
                _log?.Invoke($"[API-WARN] Move output failed, fallback copy+delete: {moveEx.Message}");
                File.Copy(engineOutput, requestedOutput, true);
                try { File.Delete(engineOutput); }
                catch (Exception deleteEx) { _log?.Invoke($"[API-WARN] Could not delete temp engine output: {deleteEx.Message}"); }
            }
            engineOutput = requestedOutput;
            finalOutput = requestedOutput;
        }

        UpdateState(job.Guid, s =>
        {
            s.Status = result.Success ? "completed" : "failed";
            s.Done = true;
            s.Success = result.Success;
            s.Progress = result.Success ? 100 : job.Progress;
            s.EngineOutput = engineOutput;
            s.FinalOutput = result.Success ? finalOutput : null;
            s.Message = result.Success ? "Render completed" : result.StatusDisplay;
            s.OutputVideo = requestedOutput.Length > 0 ? requestedOutput : s.OutputVideo;
        });
        return (result, engineOutput, finalOutput);
    }

    // Initial state: mirror khối SetApiJobState của MainWindow (5192+) — các field copy từ job.
    private void SetInitialState(RenderJob job, LocalhostRequestMapping mapped)
    {
        string jobId = job.Guid;
        string outputPath = mapped.OutputPath;
        var state = new ApiRenderJobState
        {
            QueueId = job.Id,
            JobId = jobId,
            Status = "queued",
            Done = false,
            Success = false,
            Progress = 0,
            Message = "Accepted",
            RequestedOutput = outputPath,
            InputFile = mapped.InputPath,
            InputFiles = job.MergeVideos ? new List<string>(job.MergeInputPaths) : new List<string> { job.SourcePath },
            MergeVideos = job.MergeVideos,
            AudioFile = job.AudioPath,
            AudioFile2 = job.SecondaryAudioPath,
            AudioFiles = job.MergeAudio ? new List<string>(job.MergeAudioPaths) : (job.AudioPath != null ? new List<string> { job.AudioPath } : null),
            MergeAudio = job.MergeAudio,
            AudioTrimStart = job.AudioTrimStart,
            AudioTrimEnd = job.AudioTrimEnd,
            AudioTrimDuration = job.AudioTrimDuration,
            AudioSegments = job.AudioSegments == null ? null : job.AudioSegments
                .Select(seg => new AudioEditSegment { StartSeconds = seg.StartSeconds, EndSeconds = seg.EndSeconds })
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
            ImageTimelineDurationSeconds = job.ImageTimelineDurationSeconds,
            CropZoomPercent = job.CropZoomPercent,
            CropAspectRatio = job.CropAspectRatio,
        };
        lock (_stateLock) _states[jobId] = state;
    }

    private ApiRenderJobState GetState(string jobId)
    {
        lock (_stateLock) return _states.TryGetValue(jobId, out var s) ? s : new ApiRenderJobState { JobId = jobId };
    }

    private void UpdateState(string jobId, Action<ApiRenderJobState> update)
    {
        lock (_stateLock)
        {
            if (_states.TryGetValue(jobId, out var s)) update(s);
        }
    }

    private static bool? ParseApiBoolQuery(string? value) => value?.ToLowerInvariant() switch
    {
        "1" or "true" or "yes" or "on" => true,
        "0" or "false" or "no" or "off" => false,
        _ => null,
    };

    private async Task RespondAsync(System.Net.HttpListenerContext context, int status, LocalhostRenderResponse payload)
    {
        context.Response.StatusCode = status;
        context.Response.ContentType = "application/json";
        context.Response.ContentEncoding = Encoding.UTF8;
        string json = JsonSerializer.Serialize(payload, JsonOpts);
        byte[] bytes = Encoding.UTF8.GetBytes(json);
        context.Response.ContentLength64 = bytes.Length;
        await context.Response.OutputStream.WriteAsync(bytes);
        context.Response.Close();
        _log?.Invoke($"[HOST] {context.Request.HttpMethod} {context.Request.Url?.AbsolutePath} -> {status}");
    }
}
