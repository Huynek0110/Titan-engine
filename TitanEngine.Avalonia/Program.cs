using Avalonia;
using Avalonia.Media;
using TitanEngine.Avalonia.ViewModels;

namespace TitanEngine.Avalonia;

internal static class Program
{
    // Giai đoạn 5: --screenshot <dir> chụp 13 khu; --e2e <dir> chạy vòng thật qua command của UI
    // (verify harness — không phải tính năng người dùng).
    [STAThread]
    public static int Main(string[] args)
    {
        if (args.Length >= 2 && args[0] == "--screenshot")
        {
            App.ScreenshotDir = args[1];
        }
        if (args.Length >= 2 && args[0] == "--e2e")
        {
            App.E2eDir = args[1];
            MainWindowViewModel.E2eMode = true;
        }

        var builder = AppBuilder.Configure<App>()
            .UsePlatformDetect()
            // Giai đoạn 5 mục 4: "Segoe UI" không có trên Linux — mặc định DejaVu Sans
            // (fc-list chứng minh hỗ trợ lang=vi + glyph ế/ệ/ổ), Avalonia tự fallback còn lại.
            .With(new FontManagerOptions { DefaultFamilyName = "DejaVu Sans" })
            .LogToTrace();
        return builder.StartWithClassicDesktopLifetime(args);
    }
}
