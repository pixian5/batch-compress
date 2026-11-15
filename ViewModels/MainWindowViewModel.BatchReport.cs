using System;
using System.IO;
using BatchCompress.Avalonia.Core.Services;
using CommunityToolkit.Mvvm.Input;

namespace BatchCompress.Avalonia.ViewModels;

/// <summary>
/// 「导出本次结果」界面命令。
/// WorkBuddy, 2026-11-15：批次 20（P4-3）——与命令行 --report 共用同一个报告服务，
/// 报告只从结构化进度字段生成，不读取命令日志，因此默认不含明文密码。
/// </summary>
public partial class MainWindowViewModel
{
    [RelayCommand]
    private void ExportBatchReport()
    {
        var entries = LastReportCollector?.Entries ?? [];
        if (entries.Count == 0)
        {
            AppendCommandLog($"[{L.ExportBatchReport}] {L.BatchReportNoData}\n");
            return;
        }

        try
        {
            // WorkBuddy, 2026-11-16：批次 27——默认目录与「关于」弹窗展示的值同源。
            var reportDirectory = ApplicationInfoService.ResolveReportDirectory();

            // 毫秒后缀：同一秒内连点两次导出也不会覆盖前一份报告。
            var timestamp = DateTime.Now.ToString("yyyyMMdd-HHmmss-fff");
            var targetPath = Path.Combine(reportDirectory, $"batchcompress-results-{timestamp}.csv");
            var writtenPath = BatchReportWriter.Write(
                targetPath, entries, LastReportCollector?.OmittedEntries ?? 0);
            var message = string.Format(L.BatchReportSaved, writtenPath);
            AppendCommandLog($"[{L.ExportBatchReport}] {message}\n");
            _systemIntegration.ShowNotification(L.ExportBatchReport, message);
        }
        catch (Exception ex)
        {
            var error = string.Format(L.BatchReportFailed, ex.Message);
            AppendCommandLog($"[{L.ExportBatchReport}] {error}\n");
            _systemIntegration.ShowNotification(L.ExportBatchReport, error);
        }
    }
}
