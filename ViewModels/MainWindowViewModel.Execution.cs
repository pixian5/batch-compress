using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using BatchCompress.Avalonia.Core.Models;
using BatchCompress.Avalonia.Core.Services;
using BatchCompress.Avalonia.Localization;
using CommunityToolkit.Mvvm.Input;

namespace BatchCompress.Avalonia.ViewModels;

// Antigravity, 2026-11-08：批次 15——拆分 MainWindowViewModel 超千行大文件。
// 本文件聚合压缩/解压命令执行、取消流程、关机撤销、密码查询与日志清理。
public partial class MainWindowViewModel
{
    [RelayCommand]
    private async Task CompressAsync()
    {
        if (IsOperating) return;

        if (!IsCompressionActionVisible)
        {
            AppendCommandLog(L.CompressionPageOnly + "\n");
            return;
        }

        // WorkBuddy, 2026-11-16：0.8.12——Windows 首次使用时先确保 7-Zip 就绪；
        // 其它平台立即返回，不影响启动速度。
        await EnsureArchiveToolingAsync();

        ActivateOperationStateForStart(0);

        if (ExistingFileMode == 1 && LockArchive)
        {
            var message = L.UpdateConflictsLockArchive;
            AppendCommandLog($"[{L.OptionConflict}] {message}\n");
            _systemIntegration.ShowNotification(L.OptionConflict, message);
            return;
        }

        // GLM-5.3, 2026-11-04：批次 2 阶段 3——进度回调与生命周期收敛到 OperationSession。
        var session = new OperationSession(this, _systemIntegration);
        // WorkBuddy, 2026-11-15：批次 20（P4-3）——保留本批次收集器供「导出本次结果」使用。
        LastReportCollector = session.ReportCollector;
        LastFailedRetryList = session.FailedRetryList;
        _lastOperationWasCompression = true;
        _lastOperationWasPasswordBook = false;
        _lastOperationOriginalSourceList = SourceFileList;
        // WorkBuddy, 2026-11-16：审查修复——界面侧此前从未设置操作类型，
        // 解压批次导出的报告「操作」列会全部误标成压缩。
        session.ReportCollector.SetOperation(true);
        await session.RunAsync(
            () => L.Compress,
            () => L.CompressionComplete,
            async cancellationToken =>
            {
                // GPT-5, 2026-08-05：处理前统一规范按行输入；空行绝不能成为归档任务。
                // GLM-5.3, 2026-11-04：批次 2 阶段 2——按行解析下沉到 SourceCollector，
                // 统一空行/元数据过滤、路径规范化（消除尾部斜杠缺陷）与保序去重。
                var sourcePaths = SourceCollector.CollectCompressionSourcesFromText(SourceFileList);

                // 列表为空时先尝试自动加载。
                if (sourcePaths.Count == 0)
                {
                    AppendCommandLog(L.TryingToLoadAutomatically + "\n");
                    await RefreshFileListAsync();

                    // 自动加载后再次检查列表。
                    sourcePaths = SourceCollector.CollectCompressionSourcesFromText(SourceFileList);

                    if (sourcePaths.Count == 0)
                    {
                        AppendCommandLog(L.StillNoFiles + "\n");
                        return null;
                    }
                }

                _lastOperationOriginalSourceList = SourceFileList;

                // GPT-5, 2026-08-05：在操作开始时快照选项，避免处理中修改影响在途任务。
                var options = BuildBatchOperationOptions();

                // Antigravity, 2026-11-04：批次 11——向会话注入待处理总条目数，用于提供精确 ETA 兜底估算。
                session.SetTotalItems(sourcePaths.Count);
                await _batchOperationService.BatchCompressAsync(
                    sourcePaths, options, session.CreateProgress(() => L.Compress), cancellationToken);
                return options;
            });
    }

    [RelayCommand]
    private async Task DecompressAsync()
    {
        if (IsOperating) return;

        if (!IsDecompressionActionVisible)
        {
            AppendCommandLog(L.DecompressionPageOnly + "\n");
            return;
        }

        // WorkBuddy, 2026-11-16：0.8.12——解压同样需要 7-Zip（RAR 也可能回退到 7-Zip）。
        await EnsureArchiveToolingAsync();

        ActivateOperationStateForStart(1);

        // GLM-5.3, 2026-11-04：批次 2 阶段 3——进度回调与生命周期收敛到 OperationSession。
        var session = new OperationSession(this, _systemIntegration);
        // WorkBuddy, 2026-11-15：批次 20（P4-3）——保留本批次收集器供「导出本次结果」使用。
        LastReportCollector = session.ReportCollector;
        LastFailedRetryList = session.FailedRetryList;
        _lastOperationWasCompression = false;
        _lastOperationWasPasswordBook = SourceMode == 0;
        _lastOperationOriginalSourceList = SourceFileList;
        session.ReportCollector.SetOperation(false);
        await session.RunAsync(
            () => L.Decompress,
            () => L.DecompressionComplete,
            async cancellationToken =>
            {
                // 解析来源列表；解压密码本模式包含交替密码行。
                // 列表为空时先尝试自动加载。
                if (string.IsNullOrEmpty(SourceFileList.Trim()))
                {
                    AppendCommandLog(L.TryingToLoadAutomatically + "\n");
                    await RefreshFileListAsync();

                    // 自动加载后仍为空时记录错误并返回。
                    if (string.IsNullOrEmpty(SourceFileList.Trim()))
                    {
                        AppendCommandLog(L.StillNoFiles + "\n");
                        return null;
                    }
                }

                _lastOperationOriginalSourceList = SourceFileList;

                List<FileEntry> entries = SourceMode == 0
                    // 密码本模式按文件行、密码行交替解析（SourceCollector 保留配对语义并统一路径规范化）。
                    ? SourceCollector.CollectPasswordBookEntries(SourceFileList)
                    // 其他模式只有文件路径（存在性/格式过滤＋规范化；跨条目去重仍由服务层负责以保持 IgnoreCount 口径）。
                    : SourceCollector.CollectDecompressionFilesFromText(SourceFileList, Extension);

                if (entries.Count == 0)
                {
                    AppendCommandLog(L.NoFilesToProcess + "\n");
                    return null;
                }

                var options = BuildBatchOperationOptions();

                // Antigravity, 2026-11-04：批次 11——向会话注入待处理总条目数，用于提供精确 ETA 兜底估算。
                session.SetTotalItems(entries.Count);
                await _batchOperationService.BatchDecompressAsync(
                    entries, options, session.CreateProgress(() => L.Decompress), cancellationToken);
                return options;
            });
    }

    [RelayCommand]
    private void CancelOperation()
    {
        _cancellationTokenSource?.Cancel();
        AppendCommandLog(L.CancellingOperation + "\n");
    }

    // GLM-5.3, 2026-11-04：批次 2 阶段 3——取消令牌生命周期托管给 OperationSession；取消命令经 VM 字段访问在途令牌。
    internal CancellationTokenSource BeginOperationCancellation()
    {
        _cancellationTokenSource = new CancellationTokenSource();
        return _cancellationTokenSource;
    }

    internal void EndOperationCancellation()
    {
        _cancellationTokenSource?.Dispose();
        _cancellationTokenSource = null;
    }

    // GPT-5, 2026-08-07：批处理服务已向系统提交一分钟后的关机请求；GUI 随即提供确认框和持续可用的取消按钮。
    // 无界面命令行不显示此交互，仍由操作系统的关机命令语义负责。
    internal async Task OfferShutdownCancellationAsync(BatchOperationOptions options)
    {
        if (!options.ShutdownAfterComplete)
        {
            return;
        }

        IsShutdownScheduled = true;
        AppendCommandLog(L.ShutdownScheduledLog + "\n");
        _systemIntegration.ShowNotification(L.ShutdownScheduledTitle, L.ShutdownScheduledBody);

        if (ConfirmShutdownCancellationRequested != null && await ConfirmShutdownCancellationRequested())
        {
            await CancelScheduledShutdownAsync();
        }
    }

    [RelayCommand]
    private async Task CancelScheduledShutdownAsync()
    {
        if (!IsShutdownScheduled)
        {
            return;
        }

        await _systemIntegration.CancelShutdownAsync();
        IsShutdownScheduled = false;
        AppendCommandLog(L.ShutdownCancelledLog + "\n");
        _systemIntegration.ShowNotification(L.ShutdownCancelledTitle, L.ShutdownCancelledBody);
    }

    [RelayCommand]
    private void ClearLogs()
    {
        ClearAllLogs();
    }

    [RelayCommand]
    private void ClearSourceList()
    {
        SourceFileList = string.Empty;
        if (CurrentOperationTab == 0)
        {
            _compressionTabState.SourceFileList = string.Empty;
        }
        else
        {
            _decompressionTabState.SourceFileList = string.Empty;
        }
    }

    [RelayCommand]
    // WorkBuddy, 2026-11-16：批次 22——清空必须同时清掉缓冲，否则下一次刷新会把旧内容带回来。
    private void ClearSuccessLog() => ResetSuccessLog();

    [RelayCommand]
    private void ClearFailLog() => ResetFailLog();

    [RelayCommand]
    private void ClearCommandLog() => ResetCommandLog();

    [RelayCommand]
    private void ClearAllLogs()
    {
        ClearAllSourceLists();
        SourceFileList = string.Empty;
        ClearSuccessLog();
        ClearFailLog();
        ClearCommandLog();
    }

    private void ClearAllSourceLists()
    {
        _compressionTabState.SourceFileList = string.Empty;
        _decompressionTabState.SourceFileList = string.Empty;
    }

    [RelayCommand]
    private void HideWindow()
    {
        HideWindowRequested?.Invoke();
    }

    [RelayCommand]
    private void FocusLogSearch()
    {
        FocusLogSearchRequested?.Invoke();
    }

    [RelayCommand]
    private async Task ShowHelpAsync()
    {
        if (ShowHelpRequested != null)
        {
            await ShowHelpRequested();
        }
    }

    [RelayCommand]
    private async Task QueryPasswordAsync()
    {
        var filename = PasswordQueryFileName + "." + Extension;
        var passwordName = PasswordUtility.GetPasswordSourceName(
            filename,
            PasswordNameMode == (int)Core.Models.PasswordNameMode.BaseName
                ? Core.Models.PasswordNameMode.BaseName
                : Core.Models.PasswordNameMode.ArchiveName);

        // 生成多种历史兼容密码候选。
        var results = new List<string>();
        results.Add(string.Format(L.PasswordQueryArchiveName, filename));
        results.Add(string.Format(L.PasswordQueryBasis, passwordName));
        results.Add(string.Format(L.PasswordQueryCompression, PasswordUtility.GenerateCompressionPassword(passwordName)));
        results.Add(string.Format(L.PasswordQueryDecompression, PasswordUtility.GenerateDecompressionPassword(passwordName)));
        results.Add(string.Format(L.PasswordQueryUtf8Full, PasswordUtility.MD5UTF878(filename)));
        results.Add(string.Format(L.PasswordQueryUtf8Short, PasswordUtility.MD5UTF874(filename)));
        results.Add(string.Format(L.PasswordQueryGb2312, PasswordUtility.MD5GB2312(filename)));
        results.Add(L.PasswordQueryLegacyHeader);
        results.AddRange(PasswordUtility.GetLegacyPasswordCandidates(filename));

        var finalPassword = CurrentOperationTab == 0
            ? PasswordUtility.GenerateCompressionPassword(passwordName)
            : PasswordUtility.GenerateDecompressionPassword(passwordName);
        PasswordQueryResult = finalPassword;

        AppendCommandLog(string.Join("\n", results) + "\n");

        // 将最终候选密码复制到剪贴板。
        await _systemIntegration.WriteClipboardTextAsync(finalPassword);
    }

    [RelayCommand]
    private void SetOutputSameAsSource()
    {
        if (!string.IsNullOrEmpty(SaveFilePath))
        {
            OutputPath = SaveFilePath;
            TempDirectory = SaveFilePath;
        }
    }

    // GLM-5.3, 2026-11-04：批次 2 阶段 3——会话内状态由 OperationSession 在 RunAsync 开始时重置。
    internal void ResetCounters()
    {
        SuccessCount = 0;
        FailCount = 0;
        PostProcessFailCount = 0;
        IgnoreCount = 0;
        NonExistCount = 0;
        IncompleteVolumeCount = 0;
        AmbiguousArchiveCount = 0;
        ProcessedSizeGB = 0;
        SubProgressPercent = 0;
        HasSubProgress = false;

        // 重置处理统计。
        ElapsedTime = TimeSpan.Zero;
        RemainingTime = TimeSpan.Zero;
        ProcessingSpeedMBPerSecond = 0;
        EstimatedCompletionTime = DateTime.MinValue;
    }

    private BatchOperationOptions BuildBatchOperationOptions()
    {
        // GPT-5, 2026-08-05：转换为引擎枚举或 WinRAR 单位前，钳制所有由索引驱动的控件值。
        string[] volumeUnits = { "g", "m", "k" };
        var volumeUnitIndex = VolumeUnit >= 0 && VolumeUnit < volumeUnits.Length ? VolumeUnit : 0;

        var clampedCompressionLevel = CompressionLevel >= 0 && CompressionLevel <= 5
            ? CompressionLevel : ArchiveDefaults.DefaultCompressionLevel;
        var clampedExistingFileMode = ExistingFileMode >= 0 && ExistingFileMode <= 2
            ? ExistingFileMode : 2;

        return new BatchOperationOptions
        {
            SourcePath = SourcePath,
            SourceCollectionRoot = SourceMode == 1 && Directory.Exists(SaveFilePath)
                ? SaveFilePath
                : null,
            OutputPath = OutputPath,
            Extension = Extension,
            UseRandomPassword = UseRandomPassword,
            CustomPassword = UseRandomPassword ? null : CustomPassword,
            DeleteSourceAfter = DeleteSourceAfter,
            MoveSourceAfter = MoveSourceAfter,
            SkipAlreadyProcessed = SkipAlreadyProcessed,
            MaxSizeGB = MaxSizeGB,
            ShutdownAfterComplete = ShutdownAfterComplete,
            PasswordNameMode = PasswordNameMode == (int)Core.Models.PasswordNameMode.BaseName
                ? Core.Models.PasswordNameMode.BaseName
                : Core.Models.PasswordNameMode.ArchiveName,
            CompressionLevel = (Core.Interfaces.CompressionLevel)clampedCompressionLevel,
            SolidArchive = SolidArchive && clampedCompressionLevel > 0,
            VolumeSize = EnableVolume ? VolumeSize : null,
            VolumeSizeUnit = EnableVolume ? volumeUnits[volumeUnitIndex] : null,
            QuickOpen = QuickOpen,
            TestArchive = TestArchive,
            CommentFile = EnableComment && File.Exists(CommentFilePath) ? CommentFilePath : null,
            TempDirectory = !string.IsNullOrEmpty(TempDirectory) ? TempDirectory : OutputPath,
            ExistingFileMode = (Core.Interfaces.ExistingFileMode)clampedExistingFileMode,
            StrictExtract = StrictExtract,
            RecoveryRecordPercent = Math.Clamp(RecoveryRecordPercent, 0, 100),
            MaxConcurrency = Math.Clamp(MaxConcurrency, 1, 16),
            LockArchive = LockArchive,
            AddEnclosures = AddEnclosures,
            EnclosureDirectories = AddEnclosures ?
                EnclosureList.Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries) : null
        };
    }

    // Antigravity, 2026-11-08：批次 17（P3-14）——GUI 一键导出脱敏诊断包至桌面并提示。
    [RelayCommand]
    private async Task ExportDiagnosticsAsync()
    {
        try
        {
            // WorkBuddy, 2026-11-16：批次 27——导出目录与「关于」弹窗同源；
            // 扫描范围改为界面日志目录、崩溃日志目录、命令行日志目录三者的并集。
            var exportDirectory = ApplicationInfoService.ResolveReportDirectory();

            var timestamp = DateTime.Now.ToString("yyyyMMdd-HHmmss");
            var zipPath = Path.Combine(exportDirectory, $"batchcompress-diagnostics-{timestamp}.zip");

            var exporter = new DiagnosticsExportService(
                ApplicationInfoService.ResolveDiagnosticsLogDirectories(),
                new RarArchiveEngine(),
                new SevenZipArchiveEngine());

            var exportedPath = await exporter.ExportAsync(zipPath);
            var message = string.Format(L.DiagnosticsExportSuccess, exportedPath);
            AppendCommandLog($"[{L.ExportDiagnostics}] {message}\n");
            _systemIntegration.ShowNotification(L.ExportDiagnostics, message);
        }
        catch (Exception ex)
        {
            var error = string.Format(L.DiagnosticsExportFailed, ex.Message);
            AppendCommandLog($"[{L.ExportDiagnostics}] {error}\n");
            _systemIntegration.ShowNotification(L.ExportDiagnostics, error);
        }
    }

    // Gemini 4 Hydrogen, 2026-11-27：批次 42（v0.8.31）——生成当前配置等价 CLI 命令并复制到剪贴板（绝不含自定义明文密码）。
    [RelayCommand]
    private async Task CopyCliCommandAsync()
    {
        var isCompression = CurrentOperationTab == 0;
        var sourceItems = isCompression && SourceMode == 0
            ? SourceCollector.CollectCompressionSourcesFromText(SourceFileList)
            : SourceCollector.SplitLines(SourceFileList);
        var enclosures = EnclosureList
            .Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries);

        var command = CliCommandBuilder.FormatCommand(new CliCommandBuildRequest
        {
            IsCompression = isCompression,
            SourceMode = SourceMode,
            SaveFilePath = SaveFilePath,
            SourcePath = SourcePath,
            OutputPath = OutputPath,
            SourceListItems = sourceItems,
            Extension = Extension,
            UseRandomPassword = UseRandomPassword,
            HasCustomPassword = !string.IsNullOrEmpty(CustomPassword),
            PasswordNameMode = PasswordNameMode,
            CompressionLevel = CompressionLevel,
            SolidArchive = SolidArchive,
            EnableVolume = EnableVolume,
            VolumeSize = VolumeSize,
            VolumeUnit = VolumeUnit,
            RecoveryRecordPercent = RecoveryRecordPercent,
            ExistingFileMode = ExistingFileMode,
            LockArchive = LockArchive,
            QuickOpen = QuickOpen,
            TestArchive = TestArchive,
            EnableComment = EnableComment,
            CommentFilePath = CommentFilePath,
            TempDirectory = TempDirectory,
            SkipAlreadyProcessed = SkipAlreadyProcessed,
            DeleteSourceAfter = DeleteSourceAfter,
            MoveSourceAfter = MoveSourceAfter,
            StrictExtract = StrictExtract,
            MaxSizeGB = MaxSizeGB,
            ShutdownAfterComplete = ShutdownAfterComplete,
            MaxConcurrency = MaxConcurrency,
            AddEnclosures = AddEnclosures,
            EnclosureDirectories = enclosures,
            Language = SelectedLanguage
        });

        await _systemIntegration.WriteClipboardTextAsync(command);
        AppendCommandLog(string.Format(L.CliCommandCopiedLog, command) + "\n");
    }
}
