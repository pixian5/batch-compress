using System;
using System.IO;
using System.Threading.Tasks;
using BatchCompress.Avalonia.Core.Services;
using CommunityToolkit.Mvvm.Input;

namespace BatchCompress.Avalonia.ViewModels;

/// <summary>
/// 「关于」弹窗所需的只读信息与命令。
/// WorkBuddy, 2026-11-16：批次 27（P4-4）——沿用主窗口 ViewModel 的委托式界面请求，
/// 弹窗本身只是另一个视图，直接复用同一个 DataContext，语言切换时文案自动跟随 L 刷新。
/// </summary>
public partial class MainWindowViewModel
{
    /// <summary>版本、运行时与各类产物路径的快照；构造时取一次即可，运行期不变。</summary>
    public ApplicationInfo AboutInfo { get; } = ApplicationInfoService.Create();

    [RelayCommand]
    private async Task ShowAboutAsync()
    {
        if (ShowAboutRequested != null)
        {
            await ShowAboutRequested();
        }
    }

    [RelayCommand]
    private async Task OpenAboutLogDirectoryAsync()
    {
        var path = ApplicationInfoService.ResolveGuiLogDirectory();
        try
        {
            // 日志器可能还没写过文件（例如设计器或刚启动），先建目录，
            // 否则系统文件管理器会因路径不存在而静默失败。
            Directory.CreateDirectory(path);
            await _systemIntegration.OpenFolderAsync(path);
        }
        catch (Exception ex)
        {
            var message = string.Format(L.AboutOpenFolderFailed, ex.Message);
            AppendCommandLog($"[{L.About}] {message}\n");
            _systemIntegration.ShowNotification(L.About, message);
        }
    }
}
