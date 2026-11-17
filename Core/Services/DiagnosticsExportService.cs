using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using BatchCompress.Avalonia.Core.Interfaces;

namespace BatchCompress.Avalonia.Core.Services;

/// <summary>
/// 安全诊断日志脱敏导出服务。
/// Antigravity, 2026-11-08：批次 16（P3-13）——聚合系统诊断数据与历史日志，
/// 严格脱敏所有命令行密码凭据后打包为 zip，便于故障排查与工单协作。
/// </summary>
public sealed class DiagnosticsExportService
{
    private const int MaxLogFiles = 10;
    private const int MaxCrashLogs = 3;

    private readonly IReadOnlyList<string> _logsDirectories;
    private readonly IArchiveEngine? _rarEngine;
    private readonly IArchiveEngine? _sevenZipEngine;

    /// <summary>单目录入口，保留给旧调用与测试；默认仍是应用包内的 logs。</summary>
    public DiagnosticsExportService(
        string? logsDirectory = null,
        IArchiveEngine? rarEngine = null,
        IArchiveEngine? sevenZipEngine = null)
        : this(new[] { logsDirectory ?? Path.Combine(AppContext.BaseDirectory, "logs") },
               rarEngine, sevenZipEngine)
    {
    }

    /// <summary>
    /// 多目录入口。WorkBuddy, 2026-11-16：批次 27——界面日志在用户数据目录、
    /// 崩溃日志与命令行日志在应用目录，只扫一个目录会漏掉绝大部分日志。
    /// </summary>
    public DiagnosticsExportService(
        IEnumerable<string> logsDirectories,
        IArchiveEngine? rarEngine = null,
        IArchiveEngine? sevenZipEngine = null)
    {
        ArgumentNullException.ThrowIfNull(logsDirectories);
        _logsDirectories = logsDirectories
            .Where(path => !string.IsNullOrWhiteSpace(path))
            .Distinct(OperatingSystem.IsLinux() ? StringComparer.Ordinal : StringComparer.OrdinalIgnoreCase)
            .ToArray();
        _rarEngine = rarEngine;
        _sevenZipEngine = sevenZipEngine;
    }

    /// <summary>
    /// 对日志文本中的密码凭据进行脱敏替换（-p、-hp、--password 后续值替换为 ***）。
    /// </summary>
    public static string RedactSensitiveText(string input) => CredentialRedactor.Redact(input);

    /// <summary>
    /// 导出脱敏诊断包至指定 zip 路径。
    /// </summary>
    public async Task<string> ExportAsync(string targetZipPath, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(targetZipPath);

        var destinationDir = Path.GetDirectoryName(Path.GetFullPath(targetZipPath));
        if (!string.IsNullOrEmpty(destinationDir))
        {
            Directory.CreateDirectory(destinationDir);
        }

        if (File.Exists(targetZipPath))
        {
            File.Delete(targetZipPath);
        }

        using (var zipStream = new FileStream(targetZipPath, FileMode.Create, FileAccess.Write, FileShare.None))
        using (var archive = new ZipArchive(zipStream, ZipArchiveMode.Create, leaveOpen: false))
        {
            // 1. 写入系统环境与诊断摘要
            var summary = CreateDiagnosticSummary();
            var summaryEntry = archive.CreateEntry("diagnostics_summary.json", System.IO.Compression.CompressionLevel.Optimal);
            using (var summaryStream = summaryEntry.Open())
            using (var writer = new Utf8JsonWriter(summaryStream, new JsonWriterOptions { Indented = true }))
            {
                JsonSerializer.Serialize(writer, summary);
            }

            // 2. 收集并脱敏最近日志（跨全部日志目录，合计最多 10 份）
            // WorkBuddy, 2026-11-16：批次 27——文件名前缀从 batchcompress_* 放宽到 batchcompress*，
            // 使 batchcompress-crash_*.log 这类崩溃日志也能进入诊断包。
            var logFiles = new List<string>();
            foreach (var directory in _logsDirectories)
            {
                if (!Directory.Exists(directory))
                {
                    continue;
                }

                logFiles.AddRange(
                    Directory.EnumerateFiles(directory, "batchcompress*.log*")
                        .Where(f => Regex.IsMatch(Path.GetFileName(f),
                            @"\.log(\.\d+)?(\.gz)?$", RegexOptions.CultureInvariant)));
            }

            var pathComparer = OperatingSystem.IsLinux()
                ? StringComparer.Ordinal
                : StringComparer.OrdinalIgnoreCase;
            var allLogs = logFiles.Distinct(pathComparer).ToList();

            // WorkBuddy, 2026-11-16：复查修复——崩溃日志通常比界面运行日志旧，
            // 统一按时间倒序取 10 份会被大量常规日志完全挤出包外，而它恰是最关键的排障线索。
            // 先给崩溃日志留固定名额，再用其余日志按时间补足，总数仍不超过上限。
            var crashLogs = allLogs
                .Where(path => Path.GetFileName(path)
                    .StartsWith("batchcompress-crash_", StringComparison.OrdinalIgnoreCase))
                .OrderByDescending(File.GetLastWriteTimeUtc)
                .Take(MaxCrashLogs)
                .ToList();
            var selectedCrash = new HashSet<string>(crashLogs, pathComparer);
            var regularLogs = allLogs
                .Where(path => !selectedCrash.Contains(path))
                .OrderByDescending(File.GetLastWriteTimeUtc)
                .Take(MaxLogFiles - crashLogs.Count);
            var recentLogs = crashLogs.Concat(regularLogs).ToList();

            var usedEntryNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var logFile in recentLogs)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var fileName = Path.GetFileName(logFile);
                var entryName = fileName.EndsWith(".gz", StringComparison.OrdinalIgnoreCase)
                    ? fileName[..^3] // 解包还原为纯文本存入 zip
                    : fileName;
                // 不同目录可能有同名文件，必须先消歧，否则 ZipArchive.CreateEntry 会抛重复项异常。
                entryName = MakeUniqueEntryName(entryName, usedEntryNames);

                string rawText;
                if (logFile.EndsWith(".gz", StringComparison.OrdinalIgnoreCase))
                {
                    using var fileStream = File.OpenRead(logFile);
                    using var gzip = new GZipStream(fileStream, CompressionMode.Decompress);
                    using var reader = new StreamReader(gzip, Encoding.UTF8);
                    rawText = await reader.ReadToEndAsync(cancellationToken).ConfigureAwait(false);
                }
                else
                {
                    rawText = await File.ReadAllTextAsync(logFile, Encoding.UTF8, cancellationToken).ConfigureAwait(false);
                }

                var redactedText = RedactSensitiveText(rawText);
                var logEntry = archive.CreateEntry($"logs/{entryName}", System.IO.Compression.CompressionLevel.Optimal);
                using var entryStream = logEntry.Open();
                using var textWriter = new StreamWriter(entryStream, Encoding.UTF8);
                await textWriter.WriteAsync(redactedText).ConfigureAwait(false);
            }
        }

        return targetZipPath;
    }

    private static string MakeUniqueEntryName(string fileName, HashSet<string> usedNames)
    {
        if (usedNames.Add(fileName))
        {
            return fileName;
        }

        var stem = Path.GetFileNameWithoutExtension(fileName);
        var extension = Path.GetExtension(fileName);
        for (var index = 2; ; index++)
        {
            var candidate = $"{stem}_{index}{extension}";
            if (usedNames.Add(candidate))
            {
                return candidate;
            }
        }
    }

    private object CreateDiagnosticSummary()
    {
        // WorkBuddy, 2026-11-17：0.8.17——版本与 --version/关于同源。
        // 旧实现自己向上找 VERSION 文件，但三平台打包都不复制它，导致诊断包 AppVersion=unknown。
        var appVersion = ApplicationInfoService.ReadApplicationVersion();

        return new
        {
            ExportedAtUtc = DateTime.UtcNow.ToString("o"),
            AppVersion = appVersion,
            LogDirectories = _logsDirectories.ToArray(),
            DotNetRuntime = Environment.Version.ToString(),
            OSDescription = RuntimeInformation.OSDescription,
            OSArchitecture = RuntimeInformation.OSArchitecture.ToString(),
            ProcessArchitecture = RuntimeInformation.ProcessArchitecture.ToString(),
            RarEngineAvailable = _rarEngine?.IsAvailable() ?? false,
            SevenZipEngineAvailable = _sevenZipEngine?.IsAvailable() ?? false
        };
    }
}
