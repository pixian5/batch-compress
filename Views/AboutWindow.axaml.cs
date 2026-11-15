using Avalonia.Controls;
using Avalonia.Interactivity;

namespace BatchCompress.Avalonia.Views;

/// <summary>
/// 「关于」只读信息窗口。DataContext 与主窗口共用同一个 ViewModel，
/// 因此语言切换、导出诊断与日志目录命令都无需在本窗口重复实现。
/// </summary>
public partial class AboutWindow : Window
{
    public AboutWindow()
    {
        InitializeComponent();
    }

    private void CloseButton_Click(object? sender, RoutedEventArgs e) => Close();
}
