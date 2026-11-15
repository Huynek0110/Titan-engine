using System;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Net.Http;
using System.Text;
using System.Threading.Tasks;
using Avalonia.Controls.ApplicationLifetimes;
using TitanEngine.Avalonia.Services;
using TitanEngine.Avalonia.ViewModels;

namespace TitanEngine.Avalonia;

/// <summary>
/// Giai đoạn 5 — verify harness `--e2e <outDir>` (tiêu chí xong GĐ5): mở app thật, chạy MỘT VÒNG
/// thật bằng đúng đường của UI: dán đường dẫn (fallback Hyprland, điểm 2 spec) → AddToQueueCommand →
/// StartBatchCommand → đợi DONE → ffprobe + decode sạch; rồi HOST mode qua ToggleHostCommand
/// (port qua E2E hook, bỏ dialog) → POST /api/render/wait → kiểm file. KHÔNG phải tính năng người dùng.
/// </summary>
public static class E2eRunner
{
    private static int _passes;
    private static int _fails;

    private static void Check(bool ok, string label)
    {
        if (ok) { _passes++; Console.WriteLine($"[E2E] PASS  {label}"); }
        else { _fails++; Console.WriteLine($"[E2E] FAIL  {label}"); }
    }

    private static string Run(string file, string args)
    {
        var psi = new ProcessStartInfo(file, args)
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true
        };
        using var p = Process.Start(psi)!;
        string stdout = p.StandardOutput.ReadToEnd();
        string stderr = p.StandardError.ReadToEnd();
        p.WaitForExit();
        return stdout + stderr;
    }

    public static async Task RunAndShutdownAsync(IClassicDesktopStyleApplicationLifetime desktop, string outDir)
    {
        int exit = 1;
        try
        {
            var window = desktop.MainWindow!;
            var vm = (MainWindowViewModel)window.DataContext!;
            UserPrompt.Owner = null; // E2E: Alert/Confirm in console — không mở dialog chặn harness
            vm.Engine.LogEmitted += m => Console.WriteLine("[ENGINE] " + m);
            Directory.CreateDirectory(outDir);

            string videoPath = Path.Combine(Path.GetTempPath(), "TitanEngine_E2E", "video.mp4");
            string audioPath = Path.Combine(Path.GetTempPath(), "TitanEngine_E2E", "music.mp3");
            if (!File.Exists(videoPath) || !File.Exists(audioPath))
            {
                Directory.CreateDirectory(Path.GetDirectoryName(videoPath)!);
                Console.WriteLine("[E2E] Tạo media test (testsrc2 3s + sine 440Hz 3s)…");
                Run("ffmpeg", $"-y -f lavfi -i testsrc2=size=320x240:rate=30:duration=3 -pix_fmt yuv420p \"{videoPath}\"");
                Run("ffmpeg", $"-y -f lavfi -i sine=frequency=440:duration=3 -c:a libmp3lame \"{audioPath}\"");
            }
            Check(File.Exists(videoPath) && File.Exists(audioPath), "media test sẵn sàng");

            // 1. Ô dán đường dẫn (fallback Hyprland) → đúng đường IngestPastedPath
            vm.IngestPastedPath(videoPath, "video");
            vm.IngestPastedPath(audioPath, "audio1");
            // 2. Bật Text Overlay: đo chữ bằng AvaloniaTextMetrics + drawtext tiếng Việt
            vm.TextVibe.Enable = true;
            vm.TextVibe.Text = "Xin chào TitanEngine";

            await vm.AddToQueueCommand.ExecuteAsync(null);
            Check(vm.QueueLogs.Jobs.Count == 1, $"AddToQueue tạo 1 job (thực tế {vm.QueueLogs.Jobs.Count})");
            var job = vm.QueueLogs.Jobs.Count > 0 ? vm.QueueLogs.Jobs[0] : null;

            if (job != null)
            {
                // 3. START BATCH — đúng command của nút START
                vm.QueueLogs.StartBatchCommand.Execute(null);
                var deadline = TimeSpan.FromMinutes(2);
                while (job.StatusDisplay is not ("DONE" or "FAILED") && deadline > TimeSpan.Zero)
                {
                    await Task.Delay(1000);
                    deadline -= TimeSpan.FromSeconds(1);
                }
                Check(job.StatusDisplay == "DONE", $"render xong (status={job.StatusDisplay})");
                Check(job.ResultPath != null && File.Exists(job.ResultPath), $"file output tồn tại: {job.ResultPath}");

                // Đợi batch HOÀN TOÀN kết thúc (IsBatchRunning về false) trước khi toggle host:
                // StartBatchAsync còn Task.Delay(500) + Alert trong try, IsBatchRunning chỉ về
                // false trong finally (sau đó). Gọi ToggleHostAsync sớm → "Cannot toggle host
                // mode while batch is running."
                var batchDeadline = TimeSpan.FromSeconds(30);
                while (vm.QueueLogs.IsBatchRunning && batchDeadline > TimeSpan.Zero)
                {
                    await Task.Delay(200);
                    batchDeadline -= TimeSpan.FromMilliseconds(200);
                }
                Check(!vm.QueueLogs.IsBatchRunning, "batch hoàn toàn kết thúc (IsBatchRunning=false)");

                if (job.ResultPath != null && File.Exists(job.ResultPath))
                {
                    string dur = Run("ffprobe", $"-v error -show_entries format=duration -of csv=p=0 \"{job.ResultPath}\"");
                    Check(double.TryParse(dur.Trim(), NumberStyles.Any, CultureInfo.InvariantCulture, out double d)
                        && Math.Abs(d - 3.0) < 1.2, $"ffprobe đọc được, duration={dur.Trim()}s (kỳ vọng ~3s)");
                    string decodeErr = Run("ffmpeg", $"-v error -i \"{job.ResultPath}\" -f null -");
                    Check(decodeErr.Trim().Length == 0, $"decode sạch 0 lỗi ({decodeErr.Trim().Length} ký tự lỗi)");
                }
            }

            // 4. HOST mode — đúng command HOST: OFF/ON của UI
            MainWindowViewModel.E2EHostPort = 5599;
            await vm.ToggleHostAsync();
            Check(vm.QueueLogs.IsHostRunning, "HOST ON (ToggleHostCommand)");

            string hostOut = Path.Combine(outDir, "e2e_host_out.mp4");
            string body = "{\"inputFile\": \"" + EscapeJson(videoPath)
                + "\", \"audioFiles\": [\"" + EscapeJson(audioPath)
                + "\"], \"outputVideo\": \"" + EscapeJson(hostOut) + "\"}";
            try
            {
                using var http = new HttpClient { Timeout = TimeSpan.FromMinutes(6) };
                var resp = await http.PostAsync("http://127.0.0.1:5599/api/render/wait",
                    new StringContent(body, Encoding.UTF8, "application/json"));
                string json = await resp.Content.ReadAsStringAsync();
                Check(resp.IsSuccessStatusCode, $"POST /api/render/wait → HTTP {(int)resp.StatusCode}");
                using var doc = System.Text.Json.JsonDocument.Parse(json);
                bool success = doc.RootElement.TryGetProperty("success", out var sEl) && sEl.GetBoolean();
                string? finalOutput = doc.RootElement.TryGetProperty("finalOutput", out var fEl) ? fEl.GetString() : null;
                Check(success, "host render success=true");
                Check(finalOutput != null && File.Exists(finalOutput), $"finalOutput tồn tại: {finalOutput}");
                if (finalOutput != null && File.Exists(finalOutput))
                {
                    string decodeErr = Run("ffmpeg", $"-v error -i \"{finalOutput}\" -f null -");
                    Check(decodeErr.Trim().Length == 0, "decode sạch 0 lỗi (host output)");
                }
            }
            catch (Exception ex)
            {
                Check(false, $"POST /api/render/wait: {ex.Message}");
            }

            await vm.ToggleHostAsync();
            Check(!vm.QueueLogs.IsHostRunning, "HOST OFF");

            exit = _fails == 0 ? 0 : 1;
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[E2E] EXCEPTION: {ex}");
            exit = 1;
        }
        finally
        {
            Console.WriteLine($"[E2E] xong: {_passes} pass / {_fails} fail → exit {exit}");
            desktop.Shutdown(exit);
            // E2E harness: app không được treo lại sau khi render xong. Thoát process ngay
            // để verify.sh --e2e (và publish --e2e) không bị chặt ở host/Avalonia message loop.
            Environment.Exit(exit);
        }
    }

    private static string EscapeJson(string s) => s.Replace("\\", "\\\\").Replace("\"", "\\\"");
}
