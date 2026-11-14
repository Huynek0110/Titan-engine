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
            bitmap.Render(control);
            string file = Path.Combine(outDir, $"{++index:D2}-{control.GetType().Name}.png");
            using (var fs = File.Create(file))
                bitmap.Save(fs);
            Console.WriteLine($"[SCREENSHOT] {file} ({(int)w}x{(int)h})");
        }

        Console.WriteLine($"[SCREENSHOT] xong: {index} khu → {outDir}");
        desktop.Shutdown();
    }
}
