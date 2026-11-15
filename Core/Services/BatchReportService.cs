using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using BatchCompress.Avalonia.Core.Models;

namespace BatchCompress.Avalonia.Core.Services;

/// <summary>
/// 批处理报告里的一条逐项结果。只由结构化进度字段生成，不含原始命令行与进程输出。
/// </summary>
public sealed record BatchReportEntry(
    string Source,
    string Status,
    string Message,
    double ProcessedMB,
    double ElapsedSeconds,
    string Operation);

/// <summary>
/// 从进度快照的计数增量收集逐项结果（与结构化输出器 NDJSON 同一套判定思路）。
/// WorkBuddy, 2026-11-15：批次 20（P4-3）——不需要改动执行管线即可生成历史报告。
/// </summary>
public sealed class BatchReportCollector
{
    private readonly List<BatchReportEntry> _entries = [];
    private readonly object _gate = new();
    private readonly DateTime _startedAt = DateTime.Now;

    private int _successCount;
    private int _failCount;
    private int _postProcessFailCount;
    private int _ignoreCount;
    private int _nonExistCount;
    private int _incompleteVolumeCount;
    private int _ambiguousCount;
    private double _lastProcessedMB;
    private string _operation = "压缩";

    /// <summary>设置本次批次的类型（压缩 / 解压），由调用方按当前语言传入。</summary>
    public void SetOperation(string operation) => _operation = operation ?? string.Empty;

    /// <summary>已收集条目的只读快照。</summary>
    public IReadOnlyList<BatchReportEntry> Entries
    {
        get
        {
            lock (_gate)
            {
                return _entries.ToArray();
            }
        }
    }

    /// <summary>本次收集器的起始时间，用于报告标题。</summary>
    public DateTime StartedAt => _startedAt;

    /// <summary>
    /// 处理一条进度报告。命令与进程原始输出（IsCommandOutput）整条排除，
    /// 因为那里可能包含命令行里的明文密码；这是报告默认不含凭据的第一道保证。
    /// </summary>
    public void Report(OperationProgressInfo info)
    {
        ArgumentNullException.ThrowIfNull(info);
        if (info.IsCommandOutput) return;

        lock (_gate)
        {
            var succeeded = info.SuccessCount > _successCount;
            var failed = info.FailCount > _failCount;
            var skipped = info.IgnoreCount > _ignoreCount ||
                          info.NonExistCount > _nonExistCount ||
                          info.IncompleteVolumeCount > _incompleteVolumeCount ||
                          info.AmbiguousArchiveCount > _ambiguousCount;

            _successCount = info.SuccessCount;
            _failCount = info.FailCount;
            _postProcessFailCount = info.PostProcessFailCount;
            _ignoreCount = info.IgnoreCount;
            _nonExistCount = info.NonExistCount;
            _incompleteVolumeCount = info.IncompleteVolumeCount;
            _ambiguousCount = info.AmbiguousArchiveCount;

            if (!succeeded && !failed && !skipped) return;

            var status = succeeded ? "成功" : failed ? "失败" : "跳过";
            // 归档成功但后处理失败时两个计数会同时增加，状态仍按「成功」，
            // 与结构化输出器保持一致，细节留在消息里。
            var currentMB = info.ProcessedSizeGB * 1024.0;
            var deltaMB = Math.Max(0, currentMB - _lastProcessedMB);
            _lastProcessedMB = currentMB;

            var source = string.IsNullOrWhiteSpace(info.CurrentSourcePath)
                ? info.CurrentFile
                : Path.GetFileName(info.CurrentSourcePath.TrimEnd(
                    Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar));

            _entries.Add(new BatchReportEntry(
                Sanitize(source),
                status,
                Sanitize(info.Message),
                Math.Round(deltaMB, 3),
                Math.Round(info.Elapsed.TotalSeconds, 3),
                _operation));
        }
    }

    // 第二道保证：即使将来有人把命令文本塞进用户可见消息，也只保留到不含凭据的程度。
    // 真实命令里密码常直接连写（-p明文），也可能空格分隔或写成 --password=xxx，三种都要覆盖。
    // 宁可多省略一条消息，也不能让凭据进入报告。
    private static readonly Regex CredentialPattern = new(
        @"(?:^|\s)(?:--password|-hp|-p)(?:[=:]\S*|\s+\S+|\S+)",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);

    private static string Sanitize(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return string.Empty;
        var flat = text.Replace('\r', ' ').Replace('\n', ' ').Trim();
        return CredentialPattern.IsMatch(flat) ? "（已省略含凭据的文本）" : flat;
    }
}

/// <summary>
/// 把逐项结果写成 CSV（逗号分隔值）或 Markdown 汇总表。
/// 只消费 BatchReportEntry 的结构化字段，绝不读取日志文件或命令日志文本。
/// </summary>
public static class BatchReportWriter
{
    private static readonly string[] Headers =
        ["序号", "操作", "来源", "结果", "说明", "处理量(MB)", "耗时(秒)"];

    public static string ToCsv(IReadOnlyList<BatchReportEntry> entries)
    {
        var sb = new StringBuilder();
        sb.AppendLine(string.Join(",", Headers.Select(CsvCell)));
        for (var i = 0; i < entries.Count; i++)
        {
            var e = entries[i];
            sb.AppendLine(string.Join(",",
                CsvCell((i + 1).ToString(CultureInfo.InvariantCulture)),
                CsvCell(e.Operation),
                CsvCell(e.Source),
                CsvCell(e.Status),
                CsvCell(e.Message),
                CsvCell(e.ProcessedMB.ToString("F3", CultureInfo.InvariantCulture)),
                CsvCell(e.ElapsedSeconds.ToString("F3", CultureInfo.InvariantCulture))));
        }
        return sb.ToString();
    }

    public static string ToMarkdown(IReadOnlyList<BatchReportEntry> entries)
    {
        var sb = new StringBuilder();
        sb.AppendLine("# 批处理结果报告");
        sb.AppendLine();
        sb.AppendLine("| " + string.Join(" | ", Headers) + " |");
        sb.AppendLine("| " + string.Join(" | ", Headers.Select(_ => "---")) + " |");
        for (var i = 0; i < entries.Count; i++)
        {
            var e = entries[i];
            sb.AppendLine("| " + string.Join(" | ",
                (i + 1).ToString(CultureInfo.InvariantCulture),
                MdCell(e.Operation), MdCell(e.Source), MdCell(e.Status), MdCell(e.Message),
                e.ProcessedMB.ToString("F3", CultureInfo.InvariantCulture),
                e.ElapsedSeconds.ToString("F3", CultureInfo.InvariantCulture)) + " |");
        }
        sb.AppendLine();
        sb.AppendLine($"共 {entries.Count} 项；成功 {entries.Count(e => e.Status == "成功")}，" +
                      $"失败 {entries.Count(e => e.Status == "失败")}，" +
                      $"跳过 {entries.Count(e => e.Status == "跳过")}。");
        return sb.ToString();
    }

    /// <summary>
    /// 按扩展名选择格式（.md / .markdown 为 Markdown，其余为 CSV），写入 UTF-8 文件并返回路径。
    /// CSV 带 BOM（字节顺序标记），方便直接用表格软件打开中文表头。
    /// </summary>
    public static string Write(string path, IReadOnlyList<BatchReportEntry> entries)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        var extension = Path.GetExtension(path);
        var isMarkdown = extension.Equals(".md", StringComparison.OrdinalIgnoreCase) ||
                         extension.Equals(".markdown", StringComparison.OrdinalIgnoreCase);
        var content = isMarkdown ? ToMarkdown(entries) : ToCsv(entries);

        var fullPath = Path.GetFullPath(path);
        var directory = Path.GetDirectoryName(fullPath);
        if (!string.IsNullOrEmpty(directory))
        {
            Directory.CreateDirectory(directory);
        }

        File.WriteAllText(fullPath, content, new UTF8Encoding(encoderShouldEmitUTF8Identifier: !isMarkdown));
        return fullPath;
    }

    private static string CsvCell(string? value)
    {
        var safe = (value ?? string.Empty).Replace("\"", "\"\"");
        return $"\"{safe}\"";
    }

    private static string MdCell(string? value) =>
        (value ?? string.Empty).Replace("|", "\\|").Replace("\r", " ").Replace("\n", " ");
}
