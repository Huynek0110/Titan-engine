using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Markup.Xaml;

namespace TitanEngine.Avalonia.Views;

/// <summary>Thay PortInputWindow (WPF) của BtnHostMode_Click: nhập port cho host API.</summary>
public partial class HostPortWindow : Window
{
    public HostPortWindow()
    {
        InitializeComponent();
        PortBox.Text = "5555";
        PortBox.Focus();
    }

    private void InitializeComponent() => AvaloniaXamlLoader.Load(this);

    private void Ok_Click(object? sender, RoutedEventArgs e) => Close(PortBox.Text?.Trim());

    private void Cancel_Click(object? sender, RoutedEventArgs e) => Close(null);
}
