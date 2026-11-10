using System.Text;

namespace TitanEngine.Cli;

/// <summary>
/// Headless service set cho CLI: không dialog (Prompt trả null/false — engine dùng
/// fallback/throw như nhánh gốc), text metrics xấp xỉ theo heuristic của chính
/// engine (0.55 * fontSize / ký tự, 1.2 * fontSize / dòng) để khớp fallback gốc.
/// </summary>
internal sealed class CliUserPrompt : IUserPrompt
{
    public string? PromptForFfmpegPath() => null;
    public bool Confirm(string title, string message)
    {
        Console.Error.WriteLine($"[PROMPT] {title}: {message.ReplaceLineEndings(" | ")} (headless: trả false)");
        return false;
    }
    public void Alert(string title, string message, bool error)
        => Console.Error.WriteLine($"[ALERT-{(error ? "ERROR" : "INFO")}] {title}: {message}");
}

internal sealed class CliTextMetrics : ITextMetrics
{
    public TextMeasurement Measure(string text, string fontFamilyName, double fontSize)
    {
        if (string.IsNullOrEmpty(text))
            return new TextMeasurement(0, 0, Array.Empty<double>());

        string[] lines = text.Replace("\r\n", "\n").Split('\n');
        var widths = new List<double>(lines.Length);
        foreach (string line in lines)
            widths.Add(line.Length * fontSize * 0.55);

        double height = lines.Length * fontSize * 1.2;
        return new TextMeasurement(widths.Max(), height, widths);
    }
}

internal static class CliServices
{
    public static void Install()
    {
        EngineServices.Prompt = new CliUserPrompt();
        EngineServices.Text = new CliTextMetrics();
    }
}
