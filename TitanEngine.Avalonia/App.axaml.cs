using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
using Avalonia.Styling;
using Avalonia.Themes.Fluent;
using TitanEngine.Avalonia.Services;

namespace TitanEngine.Avalonia;

public partial class App : Application
{
    /// <summary>Verify harness: đặt bởi Program khi chạy `--screenshot <dir>` — null = chạy bình thường.</summary>
    public static string? ScreenshotDir { get; set; }

    /// <summary>Verify harness: đặt bởi Program khi chạy `--e2e <dir>` — null = chạy bình thường.</summary>
    public static string? E2eDir { get; set; }

    public override void Initialize()
    {
        // FluentTheme là nền theme mặc định (Dark). Các Style/token tuỳ biến trong App.axaml
        // được nạp sau (trong Application.Styles / Application.Resources) và ghi đè từng
        // control cần đổi vỏ (HeaderedContentControl, ToolTip, Button, TextBox, ComboBox,
        // CheckBox, Slider). Các control KHÔNG có style tuỳ biến — ScrollViewer, ScrollBar,
        // ComboBox dropdown, ListBox/ListBoxItem, ContextMenu, dialog IUserPrompt — dùng
        // template mặc định của FluentTheme → vẫn cuộn chuột, hiển thị dropdown/tooltip.
        // BUGFIX mục 0: commit trước bỏ FluentTheme → các control này mất template,
        // lăn chuột không tác động, dropdown không hiện.
        Styles.Add(new FluentTheme());
        AvaloniaXamlLoader.Load(this);
        RequestedThemeVariant = ThemeVariant.Dark;
    }

    public override void OnFrameworkInitializationCompleted()
    {
        // Giai đoạn 5: cài service cho Core — text metrics ĐO THẬT bằng Avalonia TextLayout;
        // image services: decode/thuật không-vẽ chạy thật, 8 khả năng vẽ WPF-stay throw rõ ràng.
        // IUserPrompt qua StorageProvider (1 trong 5 điểm spec đã duyệt); UserPrompt.Owner
        // được MainWindow gán sau khi cửa sổ dựng xong.
        // Cài ngoài lifetime check để cả headless (screenshot harness) lẫn desktop đều có.
        EngineServices.Text = new AvaloniaTextMetrics();
        EngineServices.Images = new AvaloniaImageServices();
        EngineServices.Prompt = new AvaloniaUserPrompt();

        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            desktop.MainWindow = new Views.MainWindow();
            if (ScreenshotDir != null)
            {
                desktop.MainWindow.Opened += (_, _) => _ = ScreenshotRunner.CaptureAndShutdownAsync(desktop, ScreenshotDir);
            }
            if (E2eDir != null)
            {
                desktop.MainWindow.Opened += (_, _) => _ = E2eRunner.RunAndShutdownAsync(desktop, E2eDir);
            }
        }

        base.OnFrameworkInitializationCompleted();
    }
}
