using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Media.Imaging;
using Avalonia.VisualTree;

namespace TitanEngine.Avalonia;

/// <summary>
/// Giai đoạn 5 — verify harness: `TitanEngine.Avalonia --screenshot <dir>` mở app thật
/// (platform desktop), chụp từng khu UI ra PNG bằng RenderTargetBitmap rồi thoát.
/// Đáp ứng tiêu chí verify của spec: "ảnh chụp 12 khu". KHÔNG phải tính năng người dùng.
/// (Headless không dùng được: UseHeadlessDrawing=false không đăng ký render interface,
/// flag mặc định chỉ vẽ giả → PNG rỗng — đã thử và bỏ.)
/// </summary>
public static class ScreenshotRunner
{
    public static async Task CaptureAndShutdownAsync(IClassicDesktopStyleApplicationLifetime desktop, string outDir)
    {
        var window = desktop.MainWindow!;
        Directory.CreateDirectory(outDir);

        // Đợi layout xong (2 pass qua các tick UI).
        for (int i = 0; i < 10; i++)
            await global::Avalonia.Threading.Dispatcher.UIThread.InvokeAsync(async () => await Task.Yield());

        var khuControls = window.GetVisualDescendants()
            .OfType<UserControl>()
            .Where(c => c.GetType().Namespace!.EndsWith("Views.Controls", StringComparison.Ordinal))
            .ToList();

        int index = 0;
        foreach (var control in khuControls)
        {
            // Dùng layout THẬT của cửa sổ (ScrollViewer layout hết mọi khu, kể cả ngoài viewport).
            // Measure/Arrange tay (680, ∞) trước đây làm TextLayout wrap lệch pha → RTB trắng toàn
            // phần ở khu có TextBlock wrap 2 dòng (TextVibe) — đã chứng minh bằng bisect 13/11.
            control.UpdateLayout();

            double w = control.Bounds.Width;
            double h = control.Bounds.Height;
            if (w <= 0 || h <= 0) continue;

            using var bitmap = new RenderTargetBitmap(new PixelSize((int)Math.Ceiling(w), (int)Math.Ceiling(h)));

            // RenderTargetBitmap.Render(control) trên UserControl gốc là TemplatedControl
            // (HeaderedContentControl) → vẽ đen trên Avalonia, mặc dù cây trực quan đã layout
            // đầy đủ. Chain là ContentPresenter → [root element]. Với root là Panel
            // (StackPanel/Grid) thì vẽ được; với root là TemplatedControl (HeaderedContentControl)
            // thì offscreen RTB vẽ đen. Lấy phần tử gốc của UserControl và render trực tiếp
            // (vẫn gắn cây cửa sổ → có render root, không bị treo như render panel tách biệt).
            global::Avalonia.Visual renderTarget = control;
            var cp = control.GetVisualChildren().FirstOrDefault();
            if (cp != null)
            {
                var root = cp.GetVisualChildren().FirstOrDefault();
                if (root != null) renderTarget = root;
            }
            bitmap.Render(renderTarget);
            string file = Path.Combine(outDir, $"{++index:D2}-{control.GetType().Name}.png");
            using (var fs = File.Create(file))
                bitmap.Save(fs);
            Console.WriteLine($"[SCREENSHOT] {file} ({(int)w}x{(int)h})");
        }

        Console.WriteLine($"[SCREENSHOT] xong: {index} khu → {outDir}");

            // ── Kiểm tra cuộn chuột (mục 0 BUG KHẨN) ──
            // Mọi ScrollViewer phải có template áp dụng (có phần tử trực quan con), và
            // khi Extent > Viewport thì gọi LineDown() phải thay đổi Offset.
            int scrollFail = 0;
            int scrollIndex = 0;
            foreach (var sv in window.GetVisualDescendants().OfType<ScrollViewer>())
            {
                scrollIndex++;
                bool hasPresenter = sv.GetVisualDescendants().Any();
                bool scrollable = sv.Extent.Height > sv.Viewport.Height
                               || sv.Extent.Width > sv.Viewport.Width;
                bool offsetChanged = false;
                if (scrollable)
                {
                    var before = sv.Offset;
                    sv.LineDown();
                    // Cho layout / ScrollBar phản hồi trước khi đọc lại Offset.
                    await global::Avalonia.Threading.Dispatcher.UIThread.InvokeAsync(async () => await Task.Yield());
                    sv.UpdateLayout();
                    offsetChanged = sv.Offset != before;
                }
                bool pass = hasPresenter && (!scrollable || offsetChanged);
                if (!pass) scrollFail++;
                Console.WriteLine($"[SCROLL] #{scrollIndex} extent={sv.Extent.Width:F0}x{sv.Extent.Height:F0} " +
                                  $"viewport={sv.Viewport.Width:F0}x{sv.Viewport.Height:F0} " +
                                  $"presenter={hasPresenter} scrollable={scrollable} " +
                                  $"offsetChanged={offsetChanged} -> {(pass ? "PASS" : "FAIL")}");
            }
            Console.WriteLine($"[SCROLL] xong: {scrollIndex} ScrollViewer, {scrollFail} FAIL");

            desktop.Shutdown();
    }
}
