using System;
using System.Diagnostics;
using System.Threading;

namespace TitanEngine
{
    /// <summary>
    /// Giai đoạn 3: probe encoder bằng test encode THẬT ~1 giây (không chỉ đọc "ffmpeg -encoders").
    /// Lý do: build ffmpeg liệt kê av1_nvenc kể cả trên GPU Pascal (GTX 1050 Ti) — chỉ một
    /// lần encode thật mới nói đúng encoder có dùng được hay không.
    /// Windows KHÔNG dùng class này (giữ nguyên probe "ffmpeg -encoders" hiện có).
    /// </summary>
    public static class EncoderProbe
    {
        /// <summary>Kết quả probe Linux — các cờ VAAPI/QSV chỉ có nghĩa trên Linux.</summary>
        public sealed class LinuxResult
        {
            public bool H264Nvenc { get; init; }
            public bool HevcNvenc { get; init; }
            public bool Av1Nvenc { get; init; }
            public bool H264Vaapi { get; init; }
            public bool H264Qsv { get; init; }
            public string? VaapiRenderNode { get; init; }
        }

        /// <summary>
        /// Probe từng encoder bằng 1 lần encode thật ngắn (nguồn lavfi color 320x240, 1s).
        /// Args mirror đúng bảng chọn encoder của docs/10 §5 để probe đo đúng thứ sẽ chạy.
        /// NVENC/VAAPI/QSV fail → false → pipeline rơi xuống libx264 như fallback gốc.
        /// </summary>
        public static LinuxResult ProbeLinux(string ffmpegPath, Action<string>? onLog = null, CancellationToken token = default)
        {
            string? vaapiNode = FindVaapiRenderNode();

            // NVENC: không cần thiết bị gì thêm — driver tự chọn device.
            bool h264Nvenc = ProbeEncode(ffmpegPath, "-c:v h264_nvenc -preset medium -cq 18", null, onLog, "h264_nvenc", token);
            bool hevcNvenc = ProbeEncode(ffmpegPath, "-c:v hevc_nvenc -preset medium -cq 18", null, onLog, "hevc_nvenc", token);
            bool av1Nvenc = ProbeEncode(ffmpegPath, "-c:v av1_nvenc -preset medium -cq 18", null, onLog, "av1_nvenc", token);

            // VAAPI: cần -vaapi_device trỏ render node + format=nv12,hwupload như docs/10.
            bool h264Vaapi = false;
            if (vaapiNode != null)
            {
                string vaapiArgs = $"-init_hw_device vaapi=va0:{vaapiNode} -hwaccel_device va0 -filter_hw_device va0 " +
                                   "-vf format=nv12,hwupload ";
                h264Vaapi = ProbeEncode(ffmpegPath, vaapiArgs + "-c:v h264_vaapi -qp 20", null, onLog, "h264_vaapi", token);
            }
            else
            {
                onLog?.Invoke("[ENCODER-PROBE] No VAAPI render node (/dev/dri/renderD*) — skip h264_vaapi probe.");
            }

            bool h264Qsv = ProbeEncode(ffmpegPath, "-c:v h264_qsv -preset veryslow", null, onLog, "h264_qsv", token);

            var result = new LinuxResult
            {
                H264Nvenc = h264Nvenc,
                HevcNvenc = hevcNvenc,
                Av1Nvenc = av1Nvenc,
                H264Vaapi = h264Vaapi,
                H264Qsv = h264Qsv,
                VaapiRenderNode = vaapiNode,
            };
            onLog?.Invoke($"[ENCODER-PROBE] Linux result: h264_nvenc={h264Nvenc}, hevc_nvenc={hevcNvenc}, av1_nvenc={av1Nvenc}, h264_vaapi={h264Vaapi} (node={vaapiNode ?? "none"}), h264_qsv={h264Qsv}");
            return result;
        }

        /// <summary>Tìm render node DRM cho VAAPI (/dev/dri/renderD128 trở đi).</summary>
        public static string? FindVaapiRenderNode()
        {
            try
            {
                if (!System.IO.Directory.Exists("/dev/dri"))
                    return null;
                foreach (string entry in System.IO.Directory.GetFiles("/dev/dri", "renderD*"))
                    return entry;
            }
            catch { }
            return null;
        }

        /// <summary>1 lần encode thật: lavfi color 1s → encoder chỉ định → null muxer. Exit 0 = dùng được.</summary>
        private static bool ProbeEncode(string ffmpegPath, string encoderArgs, string? extraInputArgs, Action<string>? onLog, string tag, CancellationToken token)
        {
            try
            {
                if (string.IsNullOrEmpty(ffmpegPath) || !System.IO.File.Exists(ffmpegPath))
                    return false;

                var psi = new ProcessStartInfo
                {
                    FileName = ffmpegPath,
                    Arguments = $"-hide_banner -loglevel error {extraInputArgs ?? ""} -f lavfi -i color=c=black:s=320x240:r=30 -t 1 {encoderArgs} -f null -",
                    UseShellExecute = false,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    CreateNoWindow = true,
                };

                using var process = Process.Start(psi);
                if (process == null)
                    return false;
                string err = process.StandardError.ReadToEnd();
                process.WaitForExit(15000);

                bool ok = process.ExitCode == 0;
                if (!ok)
                    onLog?.Invoke($"[ENCODER-PROBE] {tag}: FAILED (exit {process.ExitCode}) {err.Trim()}");
                return ok;
            }
            catch (Exception ex)
            {
                onLog?.Invoke($"[ENCODER-PROBE] {tag}: FAILED ({ex.Message})");
                return false;
            }
        }
    }
}
