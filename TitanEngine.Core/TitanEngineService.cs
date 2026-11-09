using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace TitanEngine
{
    /// <summary>Báo tiến của 1 job đang chạy (phase 2 facade — mirror "p:F1%" của batch loop UI).</summary>
    public sealed record RenderProgress(double Percent, string StatusDisplay);

    /// <summary>Kết quả 1 lần chạy job thành công. Lỗi/cancel: ExecuteRenderAsync đã set trạng thái FAILED trên job rồi rethrow — facade không nuốt.</summary>
    public sealed record RenderRunResult(bool Success, string? OutputPath, string StatusDisplay);

    /// <summary>Tuỳ chọn chạy 1 job — phần "settings" host được phép đổi mỗi lần chạy.</summary>
    public sealed class TitanEngineRunOptions
    {
        /// <summary>Thư mục output ("" = mặc định Titan_Output như bản gốc).</summary>
        public string? CustomOutputFolder { get; set; }

        /// <summary>Nhận báo tiến (IProgress<RenderProgress>) ngoài job.Progress/StatusDisplay (INPC).</summary>
        public IProgress<RenderProgress>? Progress { get; set; }
    }

    /// <summary>
    /// Facade cho host không phải WPF (CLI ở Giai đoạn 3, Avalonia ở Giai đoạn 5):
    /// thêm/xoá/chạy/huỷ job, trạng thái engine, progress, log stream.
    /// Bọc nguyên API tĩnh EngineCore — KHÔNG thay đổi logic engine. Host mode HTTP thật
    /// (server + endpoints) vẫn nằm phía host; facade cung cấp đúng những gì host cần.
    /// </summary>
    public interface ITitanEngineService
    {
        // ── trạng thái ──
        bool IsInitialized { get; }
        bool IsFfprobeAvailable { get; }
        bool HasNvencSupport { get; }
        bool HasHevcNvencSupport { get; }
        bool HasAv1NvencSupport { get; }
        bool HasAv1AmfSupport { get; }
        bool HasNgxDlssSupport { get; }

        /// <summary>Resolve ffmpeg/ffprobe/upscale backend + dò GPU encoder (EngineCore.Initialize).</summary>
        void Initialize();

        // ── job: thêm / xoá ──
        /// <summary>Tạo RenderJob rỗng cho host điền config (SourcePath, OutputName, effects...).</summary>
        RenderJob CreateJob();

        /// <summary>Thêm job vào hàng đợi facade và gán Id = Count + 1 (quy ước của MainWindow:6196).</summary>
        void AddJob(RenderJob job);

        /// <summary>Xoá job khỏi hàng đợi facade. Trả false nếu không có trong hàng đợi.</summary>
        bool RemoveJob(RenderJob job);

        /// <summary>Đánh lại Id = thứ tự + 1 (quy ước của MainWindow:4056).</summary>
        void RenumberJobs();

        IReadOnlyList<RenderJob> Jobs { get; }

        // ── log stream ──
        /// <summary>Mọi log engine phát ra khi chạy job qua facade (kèm route tới EngineServices.LogSink).</summary>
        event Action<string>? LogEmitted;

        // ── chạy / huỷ ──
        /// <summary>
        /// Chạy 1 job qua EngineCore.ExecuteRenderAsync. Huỷ bằng <paramref name="token"/>
        /// (hành vi giữ nguyên bản gốc: cancel giữa chừng → job FAILED + OperationCanceledException ném lên host).
        /// Trạng thái job trước/sau chạy mirror batch loop UI ("PROCESSING..." → "DONE").
        /// </summary>
        Task<RenderRunResult> RunJobAsync(RenderJob job, TitanEngineRunOptions? options, CancellationToken token);
    }

    public sealed class TitanEngineService : ITitanEngineService
    {
        private readonly object _gate = new();
        private readonly List<RenderJob> _jobs = new();

        public event Action<string>? LogEmitted;

        public bool IsInitialized => EngineCore.IsInitialized;
        public bool IsFfprobeAvailable => EngineCore.IsFfprobeAvailable;
        public bool HasNvencSupport => EngineCore.HasNvencSupport();
        public bool HasHevcNvencSupport => EngineCore.HasHevcNvencSupport();
        public bool HasAv1NvencSupport => EngineCore.HasAv1NvencSupport();
        public bool HasAv1AmfSupport => EngineCore.HasAv1AmfSupport();
        public bool HasNgxDlssSupport => EngineCore.HasNgxDlssSupport();

        public void Initialize() => EngineCore.Initialize();

        public RenderJob CreateJob() => new RenderJob();

        public void AddJob(RenderJob job)
        {
            if (job == null) throw new ArgumentNullException(nameof(job));
            lock (_gate)
            {
                job.Id = _jobs.Count + 1;
                _jobs.Add(job);
            }
        }

        public bool RemoveJob(RenderJob job)
        {
            if (job == null) throw new ArgumentNullException(nameof(job));
            lock (_gate)
            {
                return _jobs.Remove(job);
            }
        }

        public void RenumberJobs()
        {
            lock (_gate)
            {
                for (int i = 0; i < _jobs.Count; i++)
                    _jobs[i].Id = i + 1;
            }
        }

        public IReadOnlyList<RenderJob> Jobs
        {
            get
            {
                lock (_gate)
                {
                    return _jobs.ToArray();
                }
            }
        }

        public async Task<RenderRunResult> RunJobAsync(RenderJob job, TitanEngineRunOptions? options, CancellationToken token)
        {
            if (job == null) throw new ArgumentNullException(nameof(job));

            // Mirror batch loop UI (MainWindow:4513-4537): status trước/sau + progress handler.
            job.Progress = 0;
            job.StatusDisplay = "PROCESSING...";
            job.StatusColor = "Yellow";

            IProgress<RenderProgress>? progress = options?.Progress;
            Action<double> progHandler = (p) =>
            {
                job.Progress = p;
                job.StatusDisplay = $"{p:F1}%";
                progress?.Report(new RenderProgress(p, $"{p:F1}%"));
            };

            Action<string> logHandler = (m) =>
            {
                LogEmitted?.Invoke(m);
                EngineServices.LogSink?.Log(m);
            };

            await EngineCore.ExecuteRenderAsync(job, options?.CustomOutputFolder ?? "", token, progHandler, logHandler);

            job.StatusDisplay = "DONE";
            job.StatusColor = "LimeGreen";
            job.Progress = 100;
            return new RenderRunResult(true, job.ResultPath, job.StatusDisplay);
        }
    }
}
