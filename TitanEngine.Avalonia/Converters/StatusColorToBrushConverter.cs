using System;
using System.Globalization;
using Avalonia.Data.Converters;
using Avalonia.Media;

namespace TitanEngine.Avalonia.Converters;

/// <summary>
/// WPF `Foreground="{Binding StatusColor}"` tự chuyển string→Brush qua BrushConverter
/// ("Cyan"/"Yellow"/"LimeGreen"/"Red" do engine set). Avalonia không có bước đó nên
/// thêm converter 1-1; giá trị lạ/parse lỗi → Cyan (trạng thái WAITING) như mặc định.
/// </summary>
public sealed class StatusColorToBrushConverter : IValueConverter
{
    public static readonly StatusColorToBrushConverter Instance = new();

    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        if (value is string s && !string.IsNullOrWhiteSpace(s))
        {
            try
            {
                return new SolidColorBrush(Color.Parse(s));
            }
            catch
            {
                // fall through → mặc định Cyan
            }
        }
        return new SolidColorBrush(Color.Parse("Cyan"));
    }

    public object? ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        => throw new NotSupportedException();
}
