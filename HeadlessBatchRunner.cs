using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using BatchCompress.Avalonia.Core.Interfaces;
using BatchCompress.Avalonia.Core.Models;
using BatchCompress.Avalonia.Core.Services;
using BatchCompress.Avalonia.Localization;

namespace BatchCompress.Avalonia;

/// <summary>
/// 通过命令行执行批处理的无界面运行器。
/// </summary>
// GPT-5, 2026-08-05：将命令行输入适配到 GUI 共用的 BatchOperationService，同时输出稳定的控制台信息和退出码，便于脚本与 CI 使用。
public class HeadlessBatchRunner
{
    private readonly FileLoggerService _logger;
    private readonly IArchiveEngine _archiveEngine;
    private readonly BatchOperationService _batchOperationService;
    private readonly CommandLineOptions _options;

    // GLM-5.3, 2026-11-04：批次 3——--output-format json 的 NDJSON 输出器与批处理起点时间。
    // json 模式下 stdout 只承载结构化事件流，人类可读提示（Found/Completed/Log file 等）改写 stderr。
    private readonly NdjsonOutputWriter? _structuredOutput;
    private readonly DateTime _startedAt = DateTime.Now;

    // WorkBuddy, 2026-11-15：批次 18（P4-2）——缓存 auto 解析结果，
    // 使试运行与真实执行共用同一个推荐值，且提示只输出一次。
    private int? _resolvedConcurrency;

    // WorkBuddy, 2026-11-15：批次 20（P4-3）——逐项结果报告收集器。
    // 只在结构化进度字段上累积，绝不与命令日志文本发生关系。
    private readonly BatchReportCollector _reportCollector = new();

    // WorkBuddy, 2026-11-16：0.8.13——命令行人类可读文案跟随 --lang / 系统语言。
    // DRY-RUN 清单、Completed 汇总与 DEFAULTS 行仍保持英文，作为稳定的机器可读契约。
    private static LanguageStrings L => LocalizationService.Instance.Strings;

    public HeadlessBatchRunner(CommandLineOptions options, FileLoggerService logger)
    {
        _options = options;
        _logger = logger;
        // GPT-5, 2026-08-06：无界面模式与 GUI 共用同一格式路由，避免两种入口能力不一致。
        _archiveEngine = new ArchiveEngineRouter();
        // GPT-5, 2026-09-30：把 CLI 的日志器同时注入批处理服务，使后处理失败原因等诊断进入日志文件，而不是无处可查。
        _batchOperationService = new BatchOperationService(_archiveEngine, new HeadlessSystemIntegration(_logger), _logger);
        _structuredOutput = options.OutputFormat == "json" ? new NdjsonOutputWriter(Console.Out) : null;
        // WorkBuddy, 2026-11-16：审查修复——改存语言无关标记，不再在此处硬编码中文，
        // 也不再受「语言之后才设置」的时序影响。
        _reportCollector.SetOperation(options.Compress);
    }

    /// <summary>
    /// 根据命令行选项执行批处理。
    /// WorkBuddy, 2026-11-16：审查修复——报告写入放在最外层 finally，
    /// 正常结束、取消、运行中异常、试运行、诊断导出与参数校验失败都会走到，
    /// 不会再出现「指定了 --report 却静默拿不到文件」。
    /// </summary>
    public async Task<int> RunAsync()
    {
        try
        {
            return await RunBatchAsync().ConfigureAwait(false);
        }
        finally
        {
            WriteBatchReportIfRequested();
        }
    }

    private async Task<int> RunBatchAsync()
    {
        if (!string.IsNullOrWhiteSpace(_options.ExportDiagnosticsPath))
        {
            NotifyReportModeSkipped("diagnostics");
            // WorkBuddy, 2026-11-16：批次 27——命令行诊断包也覆盖界面日志与崩溃日志，
            // 同时保留本次命令行日志文件所在目录（可能由 --log-file 指定到任意位置）。
            var diagnosticsDirectories = new List<string>(ApplicationInfoService.ResolveDiagnosticsLogDirectories());
            var currentLogDirectory = Path.GetDirectoryName(_logger.LogFilePath);
            if (!string.IsNullOrWhiteSpace(currentLogDirectory))
            {
                diagnosticsDirectories.Add(currentLogDirectory);
            }

            var exporter = new DiagnosticsExportService(
                diagnosticsDirectories,
                new RarArchiveEngine(),
                new SevenZipArchiveEngine());
            try
            {
                var zip = await exporter.ExportAsync(_options.ExportDiagnosticsPath).ConfigureAwait(false);
                Console.WriteLine(string.Format(L.CliRunnerDiagnosticsExported, zip));
                return 0;
            }
            catch (Exception ex)
            {
                _logger.LogError("Failed to export diagnostics", ex);
                Console.Error.WriteLine(string.Format(L.CliRunnerDiagnosticsFailed, ex.Message));
                return 1;
            }
        }

        _logger.LogOperation("START", $"Starting headless batch operation");
        _logger.LogOperation("MODE", _options.Compress ? "Compress" : "Decompress");
        _logger.LogOperation("SOURCE", _options.SourcePath ?? "Not specified");
        _logger.LogOperation("OUTPUT", _options.OutputPath ?? "Not specified");
        _logger.LogOperation("EXTENSION", _options.Extension);
        // WorkBuddy, 2026-11-16：0.8.12——把真正生效的默认参数写进日志，
        // 便于核对图形界面与命令行是否已统一，也便于用户排查“为什么这次没分卷”。
        _logger.LogOperation(
            "DEFAULTS",
            $"level={_options.CompressionLevel}, recovery={_options.RecoveryRecord}%, " +
            $"volume={(string.IsNullOrWhiteSpace(_options.VolumeSize) ? "none" : _options.VolumeSize + _options.VolumeUnit)}");
        _logger.LogOperation("LOG_FILE", _logger.LogFilePath);

        if (!TryResolvePassword(out var passwordError))
        {
            _logger.LogError(passwordError);
            Console.Error.WriteLine(string.Format(L.CliRunnerError, passwordError));
            return 2;
        }

        // WorkBuddy, 2026-11-16：0.8.13——来源存在性不再做「有一个缺失就整批退出」的前置校验。
        // 与界面口径一致：缺失项交给适配器逐项按“未找到”计数，其余来源照常处理。
        // 路径规范化在来源收集阶段完成，尾斜杠文件不会再被误判为不存在。

        // WorkBuddy, 2026-11-15：批次 18（P4-2）——auto 在试运行前先解析并提示，
        // 让 --dry-run 预检也能看到推荐并发度；真实执行复用同一份缓存值。
        if (_options.AutoConcurrency)
        {
            ResolveConcurrency();
        }

        if (_options.DryRun)
        {
            NotifyReportModeSkipped("dry-run");
            return RunDryRun();
        }

        // WorkBuddy, 2026-11-16：0.8.12——Windows 不随包分发 7-Zip，缺少时从官方渠道自动下载安装。
        // 放在试运行之后：--dry-run 只做预检，不应产生网络下载或安装副作用。
        await EnsureSevenZipProvisionedAsync().ConfigureAwait(false);

        if (!_archiveEngine.IsAvailable())
        {
            _logger.LogError("No supported archive engine was found.");
            Console.Error.WriteLine(L.CliRunnerNoArchiveEngine);
            return 1;
        }

        if (string.IsNullOrEmpty(_options.OutputPath))
        {
            _logger.LogError("Output path is required for headless operation.");
            Console.Error.WriteLine(L.CliRunnerOutputRequired);
            return 1;
        }

        // 解压目录由 BatchOperationService 按任务创建；这样完整性预检拦截的缺卷任务不会留下空目录。
        // 压缩仍在运行器层提前创建，便于归档引擎和附件暂存使用统一输出根目录。
        if (_options.Compress && !Directory.Exists(_options.OutputPath))
        {
            try
            {
                Directory.CreateDirectory(_options.OutputPath);
                _logger.LogOperation("CREATE_DIR", $"Created output directory: {_options.OutputPath}");
            }
            catch (Exception ex)
            {
                _logger.LogError($"Failed to create output directory: {_options.OutputPath}", ex);
                Console.Error.WriteLine(string.Format(L.CliRunnerCreateOutputFailed, ex.Message));
                return 1;
            }
        }

        var batchOptions = BuildBatchOperationOptions();
        var cts = new CancellationTokenSource();

        // GPT-5, 2026-08-05：将 Ctrl+C 转为协作式取消，使 WinRAR 子进程能够执行清理流程。
        Console.CancelKeyPress += (_, e) =>
        {
            e.Cancel = true;
            _logger.LogWarning("Operation cancelled by user (Ctrl+C)");
            WriteLine("\n" + L.CliRunnerCancelling);
            cts.Cancel();
        };

        var progressInfo = new OperationProgressInfo();
        // GPT-5, 2026-08-06：CLI 没有 UI 同步上下文，内联报告可保证输出顺序和最终计数稳定。
        var progress = new InlineProgress<OperationProgressInfo>(info =>
        {
            progressInfo = info;
            // WorkBuddy, 2026-11-15：批次 20（P4-3）——与 NDJSON 用同一份进度快照收集逐项结果。
            _reportCollector.Report(info);
            var status = info.IsError ? "ERROR" : "INFO";
            _logger.LogOperation(status, info.Message);

            // GLM-5.3, 2026-11-04：批次 3——json 模式把进度交给 NDJSON 输出器
            // （命令输出含密码，整条排除）；人类可读行不再进入 stdout。
            if (_structuredOutput != null)
            {
                _structuredOutput.Report(info);
                return;
            }

            // WorkBuddy, 2026-11-15：批次 19（P4-1）——默认模式也要让用户看到失败原因，
            // 否则终端只有「1 failed」、损坏定位只能翻日志。写 stderr 不污染 stdout 的进度与计数。
            if (info.IsError && !_options.Verbose)
            {
                Console.Error.WriteLine(info.Message);
            }

            if (_options.Verbose)
            {
                WriteLine($"[{status}] {info.Message}");
            }
            else if (!_options.Quiet)
            {
                // 非详细模式只显示一行可刷新的简要进度。
                Console.Write(FormatProgressLine(info));
            }
        });

        try
        {
            if (_options.Compress)
            {
                await RunCompressAsync(batchOptions, progress, cts.Token);
            }
            else if (_options.Decompress)
            {
                await RunDecompressAsync(batchOptions, progress, cts.Token);
            }

            WriteLine(string.Empty);

            // GLM-5.3, 2026-11-04：批次 3——json 模式在批次正常结束时输出 summary 事件
            // （取消中止不输出）；退出码映射保持 0/1/130 约定不变。
            _structuredOutput?.WriteSummary(DateTime.Now - _startedAt);
            var summary = $"Completed: Success={progressInfo.SuccessCount}, Failed={progressInfo.FailCount}, " +
                         $"PostProcessFailed={progressInfo.PostProcessFailCount}, Skipped={progressInfo.IgnoreCount}, " +
                         $"NotFound={progressInfo.NonExistCount}, IncompleteVolumes={progressInfo.IncompleteVolumeCount}, " +
                         $"Ambiguous={progressInfo.AmbiguousArchiveCount}";
            _logger.LogOperation("COMPLETE", summary);
            WriteLine(summary);
            WriteLine(string.Format(L.CliRunnerLogFile, _logger.LogFilePath));

            return progressInfo.FailCount > 0 ||
                   progressInfo.PostProcessFailCount > 0 ||
                   progressInfo.IncompleteVolumeCount > 0 ||
                   progressInfo.AmbiguousArchiveCount > 0
                ? 1
                : progressInfo.SuccessCount > 0 || progressInfo.IgnoreCount > 0 ? 0 : 1;
        }
        catch (OperationCanceledException)
        {
            _logger.LogWarning("Operation was cancelled");
            WriteLine("\n" + L.CliRunnerCancelled);
            return 130;
        }
        catch (Exception ex)
        {
            _logger.LogError("Unhandled exception during operation", ex);
            Console.Error.WriteLine("\n" + string.Format(L.CliRunnerError, ex.Message));
            return 1;
        }
    }

    /// <summary>
    /// 写出逐项结果报告。由最外层 finally 调用，因此取消与异常路径也会留下部分结果。
    /// 报告是附加产物：写失败只记录错误与告警，绝不改变本批次自身的退出码。
    /// </summary>
    private void WriteBatchReportIfRequested()
    {
        if (string.IsNullOrWhiteSpace(_options.ReportPath))
        {
            return;
        }

        try
        {
            var reportPath = BatchReportWriter.Write(
                _options.ReportPath!, _reportCollector.Entries, _reportCollector.OmittedEntries);
            _logger.LogOperation("REPORT",
                $"Wrote batch report: {reportPath} ({_reportCollector.Entries.Count} items)");
            Console.Error.WriteLine(string.Format(
                L.CliRunnerReportWritten, reportPath, _reportCollector.Entries.Count));
        }
        catch (Exception ex)
        {
            _logger.LogError($"Failed to write batch report: {_options.ReportPath}", ex);
            Console.Error.WriteLine(string.Format(L.CliRunnerReportWriteFailed, ex.Message));
        }
    }

    /// <summary>试运行与诊断导出不产生逐项结果，明确告知用户报告为何只有表头。</summary>
    private void NotifyReportModeSkipped(string mode)
    {
        if (string.IsNullOrWhiteSpace(_options.ReportPath))
        {
            return;
        }

        Console.Error.WriteLine(string.Format(
            LocalizationService.Instance.Strings.CliReportModeSkipped, mode, _options.ReportPath));
    }

    private async Task RunCompressAsync(BatchOperationOptions options, IProgress<OperationProgressInfo> progress, CancellationToken cancellationToken)
    {
        var sourcePaths = CollectCompressionSources();

        _logger.LogOperation("FILES_FOUND", $"Found {sourcePaths.Count} files to compress");
        WriteLine(string.Format(L.CliRunnerFoundCompress, sourcePaths.Count));

        if (sourcePaths.Count == 0)
        {
            _logger.LogWarning("No files found to compress");
            WriteLine(L.CliRunnerNoCompressSources);
            return;
        }

        await _batchOperationService.BatchCompressAsync(sourcePaths, options, progress, cancellationToken);
    }

    private async Task RunDecompressAsync(BatchOperationOptions options, IProgress<OperationProgressInfo> progress, CancellationToken cancellationToken)
    {
        var entries = CollectDecompressionEntries();

        _logger.LogOperation("FILES_FOUND", $"Found {entries.Count} files to decompress");
        WriteLine(string.Format(L.CliRunnerFoundDecompress, entries.Count));

        if (entries.Count == 0)
        {
            _logger.LogWarning("No files found to decompress");
            WriteLine(L.CliRunnerNoDecompressSources);
            return;
        }

        await _batchOperationService.BatchDecompressAsync(entries, options, progress, cancellationToken);
    }

    // GPT-5, 2026-08-06：直接密码、密码文件与标准输入在解析层已保证互斥。
    // 此处只解析选中的秘密来源；归档命令和原始进程输出由统一批处理日志链路按用户要求原样记录。
    private bool TryResolvePassword(out string error)
    {
        error = string.Empty;
        try
        {
            if (!string.IsNullOrWhiteSpace(_options.PasswordFile))
            {
                _options.Password = File.ReadLines(_options.PasswordFile).FirstOrDefault() ?? string.Empty;
                _options.UseRandomPassword = false;
            }
            else if (_options.ReadPasswordFromStandardInput)
            {
                _options.Password = Console.In.ReadLine();
                _options.UseRandomPassword = false;
            }

            if (_options.ReadPasswordFromStandardInput && _options.Password == null)
            {
                error = LocalizationService.Instance.Strings.CliErrorPasswordReadFailed;
                return false;
            }

            return true;
        }
        catch (Exception ex)
        {
            error = string.Format(L.CliRunnerPasswordReadFailed, ex.Message);
            return false;
        }
    }

    private int RunDryRun()
    {
        var paths = _options.Compress
            ? CollectCompressionSources()
            : CollectDecompressionEntries().Select(entry => entry.FilePath).ToList();

        WriteLine($"DRY-RUN mode={(_options.Compress ? "compress" : "extract")} format={_options.Extension}");
        WriteLine($"Output: {_options.OutputPath}");
        foreach (var path in paths)
        {
            WriteLine(path);
        }

        WriteLine($"Total: {paths.Count}. No files or directories were changed.");

        // WorkBuddy, 2026-11-16：0.8.13——真实执行改为逐项计数后，试运行仍保留“预检”语义：
        // 只要请求的来源里有不存在的项，就明确列出并返回参数错误码 2，
        // 避免用户把“试运行成功”误当成“真实执行也会成功”。
        var missing = paths
            .Where(path => !File.Exists(path) && !Directory.Exists(path))
            .Distinct(OperatingSystem.IsLinux() ? StringComparer.Ordinal : StringComparer.OrdinalIgnoreCase)
            .ToList();
        if (missing.Count > 0)
        {
            foreach (var path in missing)
            {
                WriteLine(string.Format(L.ItemNotFound, path));
            }

            return 2;
        }

        return paths.Count > 0 ? 0 : 1;
    }

    private List<string> CollectCompressionSources()
    {
        var paths = new List<string>();
        if (!string.IsNullOrWhiteSpace(_options.SourcePath))
        {
            if (Directory.Exists(_options.SourcePath))
            {
                paths.AddRange(_batchOperationService.LoadCompressionSourcesFromFolder(
                    _options.SourcePath,
                    _options.SkipProcessed));
            }
            else
            {
                // 文件或缺失项都作为单个来源：缺失由适配器按 ItemNotFound 计数，
                // 不再像以前那样在收集阶段静默丢弃。
                paths.Add(_options.SourcePath);
            }
        }

        // GPT-5, 2026-08-06：--input 表示精确项目，所以指定目录本身作为一个归档来源，不展开其直接子项。
        paths.AddRange(_options.InputPaths);

        // GLM-5.3, 2026-11-04：批次 2 阶段 2——过滤/规范化/去重统一下沉到 SourceCollector，
        // 尾部斜杠路径在 CLI 侧同样被规范化。存在性不再前置校验：
        // 缺失项（包括竞态下被删除的文件）统一进入服务的 ItemNotFound 报告。
        return SourceCollector.CollectCompressionSources(paths);
    }

    private List<FileEntry> CollectDecompressionEntries()
    {
        var seedEntries = new List<FileEntry>();
        if (!string.IsNullOrWhiteSpace(_options.TextFile))
        {
            var sourceDirectory = Directory.Exists(_options.SourcePath)
                ? _options.SourcePath
                : Path.GetDirectoryName(Path.GetFullPath(_options.TextFile)) ?? Directory.GetCurrentDirectory();
            var textEntries = _batchOperationService.LoadFilesFromTextFile(
                _options.TextFile,
                sourceDirectory,
                _options.Extension);
            seedEntries.AddRange(textEntries);
            _logger.LogOperation("FILES_LOADED", $"Loaded {textEntries.Count} files from text file");
        }

        // GPT-5, 2026-08-06：TXT 模式中的 --source 只作为相对文件名的基准目录，不能再把整个目录重复加入任务。
        var rawPaths = new List<string>();
        if (string.IsNullOrWhiteSpace(_options.TextFile) && !string.IsNullOrWhiteSpace(_options.SourcePath))
        {
            rawPaths.Add(_options.SourcePath);
        }

        rawPaths.AddRange(_options.InputPaths);

        // GLM-5.3, 2026-11-04：批次 2 阶段 2——路径→任务条目（文件格式校验/首卷折叠/目录展开）、
        // 元数据过滤与全路径去重统一下沉到 SourceCollector。
        var entries = SourceCollector.CollectDecompressionEntries(
            seedEntries,
            rawPaths,
            _options.Extension,
            _options.SkipProcessed,
            _batchOperationService);

        // WorkBuddy, 2026-11-16：0.8.13——SourceCollector 会静默丢弃不存在的路径；
        // 这里把缺失来源补成条目，让适配器按 ItemNotFound 计数，与界面的逐项处理口径一致。
        AppendMissingDecompressionSources(entries, rawPaths);
        return entries;
    }

    private static void AppendMissingDecompressionSources(List<FileEntry> entries, IEnumerable<string> rawPaths)
    {
        var known = new HashSet<string>(
            entries.Select(entry => entry.FilePath),
            OperatingSystem.IsLinux() ? StringComparer.Ordinal : StringComparer.OrdinalIgnoreCase);

        foreach (var rawPath in rawPaths)
        {
            var normalized = SourceCollector.NormalizePath(rawPath);
            if (normalized.Length == 0 || SystemMetadataFileFilter.ShouldSkip(normalized))
            {
                continue;
            }

            if (File.Exists(normalized) || Directory.Exists(normalized))
            {
                continue;
            }

            if (known.Add(normalized))
            {
                entries.Add(new FileEntry { FilePath = normalized });
            }
        }
    }

    private void WriteLine(string message)
    {
        if (!_options.Quiet)
        {
            // GLM-5.3, 2026-11-04：批次 3——json 模式下 stdout 专用于 NDJSON 事件流，
            // 这些人类可读提示改写入 stderr；--quiet 时两种模式都保持静默。
            if (_structuredOutput != null)
            {
                Console.Error.WriteLine(message);
            }
            else
            {
                Console.WriteLine(message);
            }
        }
    }

    private sealed class InlineProgress<T> : IProgress<T>
    {
        private readonly Action<T> _handler;

        public InlineProgress(Action<T> handler)
        {
            _handler = handler;
        }

        public void Report(T value) => _handler(value);
    }

    /// <summary>
    /// 解析本批次实际使用的并发度。
    /// WorkBuddy, 2026-11-15：批次 18（P4-2）——auto 模式依据处理器数与可用内存推荐，
    /// 并把判断依据写进日志与标准错误（stderr）；走 stderr 是为了不污染
    /// --output-format json 的标准输出（stdout）事件流。
    /// </summary>
    private int ResolveConcurrency()
    {
        if (!_options.AutoConcurrency)
        {
            return _options.MaxConcurrency;
        }

        // 试运行路径会先调用一次；真实执行再调用时直接复用，不重复读硬件也不重复打印。
        if (_resolvedConcurrency.HasValue)
        {
            return _resolvedConcurrency.Value;
        }

        var recommendation = ConcurrencyAdvisor.Recommend();
        var memoryGB = ConcurrencyAdvisor.ToGigabytes(recommendation.AvailableMemoryBytes);
        _logger.LogOperation("CONCURRENCY",
            $"auto concurrency={recommendation.Recommended}, cores={recommendation.CoreCount}, " +
            $"availableMemoryGB={memoryGB:F1}, coreLimit={recommendation.CoreLimit}, " +
            $"memoryLimit={recommendation.MemoryLimit}");

        Console.Error.WriteLine(string.Format(
            LocalizationService.Instance.Strings.CliAutoConcurrency,
            recommendation.Recommended,
            recommendation.CoreCount,
            memoryGB.ToString("F1")));

        _resolvedConcurrency = recommendation.Recommended;
        return recommendation.Recommended;
    }

    /// <summary>
    /// Windows 上缺少 7-Zip 时，从官方渠道自动下载安装；其他平台不做任何事。
    /// 安装失败只告警，不中断批次：若机器上还有 WinRAR，RAR 任务仍可继续。
    /// </summary>
    private async Task EnsureSevenZipProvisionedAsync()
    {
        if (!OperatingSystem.IsWindows() || SevenZipProvisioner.FindExistingExecutable() != null)
        {
            return;
        }

        try
        {
            var progress = new Progress<string>(message =>
            {
                _logger.LogOperation("7ZIP_PROVISION", message);
                // 结构化输出模式下 stdout 只承载 JSON，进度提示走 stderr。
                Console.Error.WriteLine(message);
            });

            var result = await SevenZipProvisioner.EnsureAsync(progress).ConfigureAwait(false);
            _logger.LogOperation("7ZIP_PROVISION", result.Message);
            if (!result.Success)
            {
                Console.Error.WriteLine(string.Format(L.CliRunnerProvisionWarning, result.Message));
            }
        }
        catch (Exception ex)
        {
            _logger.LogError("7-Zip auto provisioning failed", ex);
            Console.Error.WriteLine(string.Format(L.CliRunnerProvisionException, ex.Message));
        }
    }

    private BatchOperationOptions BuildBatchOperationOptions()
    {
        var existingMode = _options.ExistingFileMode.ToLowerInvariant() switch
        {
            "skip" => ExistingFileMode.Skip,
            "update" => ExistingFileMode.Update,
            _ => ExistingFileMode.Overwrite
        };

        var compressionLevel = _options.CompressionLevel switch
        {
            0 => CompressionLevel.Store,
            1 => CompressionLevel.Fastest,
            2 => CompressionLevel.Fast,
            3 => CompressionLevel.Normal,
            4 => CompressionLevel.Good,
            5 => CompressionLevel.Best,
            _ => CompressionLevel.Normal
        };

        return new BatchOperationOptions
        {
            SourcePath = _options.SourcePath ?? string.Empty,
            OutputPath = _options.OutputPath ?? string.Empty,
            Extension = _options.Extension,
            UseRandomPassword = _options.UseRandomPassword,
            CustomPassword = _options.UseRandomPassword ? null : _options.Password,
            DeleteSourceAfter = _options.DeleteSource,
            MoveSourceAfter = _options.MoveSource,
            SkipAlreadyProcessed = _options.SkipProcessed,
            MaxSizeGB = _options.MaxSizeGB,
            // GLM-5.3, 2026-11-04：批次 5——CLI --concurrency/-j 透传到批处理管线。
            // WorkBuddy, 2026-11-15：批次 18（P4-2）——auto 时由硬件推荐值替代显式数值。
            MaxConcurrency = ResolveConcurrency(),
            ShutdownAfterComplete = _options.ShutdownAfter,
            PasswordNameMode = _options.PasswordName == "base"
                ? PasswordNameMode.BaseName
                : PasswordNameMode.ArchiveName,
            CompressionLevel = compressionLevel,
            SolidArchive = _options.Solid && _options.CompressionLevel > 0,
            VolumeSize = _options.VolumeSize,
            VolumeSizeUnit = _options.VolumeUnit,
            QuickOpen = _options.QuickOpen,
            TestArchive = _options.TestArchive,
            CommentFile = !string.IsNullOrEmpty(_options.CommentFile) && File.Exists(_options.CommentFile)
                ? _options.CommentFile
                : null,
            TempDirectory = !string.IsNullOrEmpty(_options.TempDir) ? _options.TempDir : _options.OutputPath,
            ExistingFileMode = existingMode,
            RecoveryRecordPercent = _options.RecoveryRecord,
            LockArchive = _options.LockArchive,
            AddEnclosures = _options.AddEnclosures,
            EnclosureDirectories = _options.AddEnclosures
                ? _options.EnclosurePaths
                    .Concat((_options.EnclosureList ?? string.Empty)
                        .Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries))
                    .Where(path => !string.IsNullOrWhiteSpace(path))
                    .Distinct(StringComparer.OrdinalIgnoreCase)
                    .ToArray()
                : null
        };
    }

    /// <summary>
    /// 格式化非详细模式下的单行刷新进度文本。
    /// 保留既有 Processed 前缀契约，附带当前条目名称（若有）。
    /// </summary>
    public static string FormatProgressLine(OperationProgressInfo info)
    {
        var currentSuffix = !string.IsNullOrWhiteSpace(info.CurrentFile)
            ? $" | {Path.GetFileName(info.CurrentFile)}"
            : string.Empty;
        // WorkBuddy, 2026-11-16：0.8.13——单行进度也跟随当前语言；
        // 行尾空格用于覆盖上一次更长的输出，保持原契约。
        return "\r" + string.Format(
            LocalizationService.Instance.Strings.CliProgressLine,
            info.SuccessCount,
            info.FailCount,
            info.PostProcessFailCount,
            info.IgnoreCount,
            currentSuffix) + "    ";
    }
}

/// <summary>
/// 无界面模式的系统集成实现：记录日志，不显示桌面界面。
/// </summary>
internal class HeadlessSystemIntegration : ISystemIntegration
{
    private readonly FileLoggerService _logger;
    // WorkBuddy, 2026-11-16：0.8.12——命令行关机改为真正调用系统命令，与界面共用同一服务。
    private readonly SystemPowerService _powerService = new();

    public HeadlessSystemIntegration(FileLoggerService logger)
    {
        _logger = logger;
    }

    public Task OpenFolderAsync(string path)
    {
        _logger.LogOperation("OPEN_FOLDER", path);
        return Task.CompletedTask;
    }

    public Task<string?> ReadClipboardTextAsync()
    {
        return Task.FromResult<string?>(null);
    }

    public Task WriteClipboardTextAsync(string text)
    {
        _logger.LogOperation("CLIPBOARD", text);
        return Task.CompletedTask;
    }

    public void ShowNotification(string title, string message)
    {
        _logger.LogOperation("NOTIFICATION", $"{title}: {message}");
        Console.WriteLine($"[{title}] {message}");
    }

    public async Task ShutdownAsync()
    {
        _logger.LogOperation("SHUTDOWN", "Requesting system shutdown with a 60-second delay");
        var accepted = await _powerService.ShutdownAsync().ConfigureAwait(false);
        _logger.LogOperation("SHUTDOWN", accepted ? "System shutdown accepted" : "System shutdown failed");
        // 结构化输出模式下标准输出只承载 JSON 事件，提示统一走标准错误。
        var strings = LocalizationService.Instance.Strings;
        Console.Error.WriteLine(accepted ? strings.CliRunnerShutdownScheduled : strings.CliRunnerShutdownFailed);
    }

    public Task CancelShutdownAsync()
    {
        _logger.LogOperation("CANCEL_SHUTDOWN", "Shutdown cancellation is not exposed in headless mode");
        return Task.CompletedTask;
    }
}
