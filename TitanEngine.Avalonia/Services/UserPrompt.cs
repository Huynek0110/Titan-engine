using System;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Platform.Storage;
using TitanEngine.Core;

namespace TitanEngine.Avalonia.Services;

/// <summary>
/// Điểm 3 đã duyệt: dialog qua StorageProvider + hộp thoại thông báo Avalonia thuần.
/// Static facade cho UI (Alert/Confirm), cài IUserPrompt cho engine (thay MessageBox.Show WPF).
/// </summary>
public static class UserPrompt
{
    /// <summary>Window chủ — App gắn khi tạo MainWindow.</summary>
    public static Window? Owner { get; set; }

    public static void Alert(string title, string message, bool error = false)
        => _ = ShowMessageAsync(title, message, error, false);

    public static bool Confirm(string title, string message)
        => ShowMessageAsync(title, message, false, true).GetAwaiter().GetResult();

    public static async Task<bool> ShowMessageAsync(string title, string message, bool error, bool askYesNo)
    {
        if (Owner == null)
        {
            Console.WriteLine($"[{title}] {message}");
            return false;
        }

        var textBlock = new TextBlock
        {
            Text = message,
            TextWrapping = TextWrapping.Wrap,
            MaxWidth = 420,
            Margin = new Thickness(0, 0, 0, 16),
            Foreground = new SolidColorBrush(Color.Parse("#F0F4F8"))
        };

        var okButton = new Button { Content = "OK", MinWidth = 90, HorizontalContentAlignment = HorizontalAlignment.Center };
        var yesButton = new Button { Content = "Yes", MinWidth = 90, HorizontalContentAlignment = HorizontalAlignment.Center };
        var noButton = new Button { Content = "No", MinWidth = 90, HorizontalContentAlignment = HorizontalAlignment.Center };

        var buttons = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            HorizontalAlignment = HorizontalAlignment.Right,
            Spacing = 10
        };
        bool result = false;
        if (askYesNo)
        {
            buttons.Children.Add(yesButton);
            buttons.Children.Add(noButton);
        }
        else
        {
            buttons.Children.Add(okButton);
        }

        var panel = new StackPanel { Margin = new Thickness(20) };
        panel.Children.Add(textBlock);
        panel.Children.Add(buttons);

        var dialog = new Window
        {
            Title = title,
            SizeToContent = SizeToContent.WidthAndHeight,
            CanResize = false,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
            Background = new SolidColorBrush(Color.Parse("#11141A")),
            Content = panel,
            ShowInTaskbar = false
        };

        var tcs = new TaskCompletionSource<bool>();
        okButton.Click += (_, _) => { result = false; dialog.Close(); };
        yesButton.Click += (_, _) => { result = true; dialog.Close(); };
        noButton.Click += (_, _) => { result = false; dialog.Close(); };
        dialog.Closed += (_, _) => tcs.TrySetResult(result);

        await dialog.ShowDialog(Owner);
        return await tcs.Task;
    }
}

/// <summary>IUserPrompt cho engine (thay MessageBox WPF trong EngineCore).</summary>
public sealed class AvaloniaUserPrompt : IUserPrompt
{
    public string? PromptForFfmpegPath()
    {
        // Thay OpenFileDialog "FFmpeg|ffmpeg.exe" của WPF — StorageProvider cần window chủ.
        var owner = UserPrompt.Owner;
        if (owner == null) return null;
        var files = owner.StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = "Chọn ffmpeg",
            AllowMultiple = false,
            FileTypeFilter = new[]
            {
                new FilePickerFileType("FFmpeg") { Patterns = new[] { "ffmpeg*", "*" } },
            }
        }).GetAwaiter().GetResult();
        return files.Count > 0 ? files[0].Path.LocalPath : null;
    }

    public bool Confirm(string title, string message) => UserPrompt.Confirm(title, message);

    public void Alert(string title, string message, bool error) => UserPrompt.Alert(title, message, error);
}
