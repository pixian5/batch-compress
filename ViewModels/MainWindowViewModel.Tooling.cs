using System;
using System.Threading.Tasks;
using BatchCompress.Avalonia.Core.Services;

namespace BatchCompress.Avalonia.ViewModels;

// WorkBuddy, 2026-11-16：0.8.12——Windows 不随包分发 7-Zip，
// 界面启动后在后台检查并按需从官方渠道下载安装；同一进程内只尝试一次。
public partial class MainWindowViewModel
{
    private Task<SevenZipProvisionResult>? _archiveToolingTask;

    /// <summary>
    /// 确保当前平台具备归档程序。非 Windows 直接返回；Windows 首次调用时在后台安装，
    /// 后续调用复用同一个任务，不会重复下载。
    /// </summary>
    internal Task<SevenZipProvisionResult> EnsureArchiveToolingAsync()
    {
        if (!OperatingSystem.IsWindows())
        {
            return Task.FromResult(new SevenZipProvisionResult(
                true, true, null, "No provisioning required on this platform."));
        }

        return _archiveToolingTask ??= RunArchiveToolingProvisionAsync();
    }

    private async Task<SevenZipProvisionResult> RunArchiveToolingProvisionAsync()
    {
        try
        {
            // Progress 必须在这里创建，才能把回调投递回界面线程。
            var progress = new Progress<string>(message => AppendCommandLog($"[7-Zip] {message}\n"));
            var result = await Task.Run(() => SevenZipProvisioner.EnsureAsync(progress));
            AppendCommandLog($"[7-Zip] {result.Message}\n");

            if (!result.Success)
            {
                _systemIntegration.ShowNotification(
                    "7-Zip",
                    string.Format(L.SevenZipAutoInstallFailed, result.Message));
            }
            else if (!result.AlreadyAvailable)
            {
                _systemIntegration.ShowNotification(
                    "7-Zip",
                    string.Format(L.SevenZipAutoInstalled, result.ExecutablePath ?? string.Empty));
            }

            return result;
        }
        catch (Exception ex)
        {
            AppendCommandLog($"[7-Zip] {ex.Message}\n");
            _systemIntegration.ShowNotification(
                "7-Zip",
                string.Format(L.SevenZipAutoInstallFailed, ex.Message));
            return new SevenZipProvisionResult(false, false, null, ex.Message);
        }
    }
}
