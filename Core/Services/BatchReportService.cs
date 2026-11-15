using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using BatchCompress.Avalonia.Core.Models;
using BatchCompress.Avalonia.Localization;

namespace BatchCompress.Avalonia.Core.Services;

/// <summary>
/// 批处理报告里的一条逐项结果。只由结构化进度字段生成，不含原始命令行与进程输出。
/// WorkBuddy, 2026-11-16：审查修复——Status / Operation 改存语言无关的中性标记，
/// 文案在写出时按当前语言翻译，避免语言设置前后求值不一致造成的混排。
/// </summary>
public sealed record BatchReportEntry(
    string Source,
    string Status,
    string Message,
    double ProcessedMB,
    double ElapsedSeconds,
    string Operation);

/// <summary>
/// 报告写出时使用的整套文案，全部来自本地化资源。
/// 服务层不再硬编码中文，五种语言导出的报告表头、状态与汇总句都跟随界面语言。
/// </summary>
public sealed class BatchReportTexts
{
    public string Title { get; init; } = "批处理结果报告";
    public string[] Headers { get; init; } =
        ["序号", "操作", "来源", "结果", "说明", "处理量(MB)", "耗时(秒)"];
    public string Success { get; init; } = "成功";
    public string Failed { get; init; } = "失败";
    public string Skipped { get; init; } = "跳过";
    public string Compress { get; init; } = "压缩";
    public string Extract { get; init; } = "解压";
    public string NoteRow { get; init; } = "说明";
    public string SummaryFormat { get; init; } = "共 {0} 项；成功 {1}，失败 {2}，跳过 {3}。";
    public string OmittedNoteFormat { get; init; } = "另有 {0} 条因超出上限未列出，请缩小批次或分批执行";
    public string CredentialRedacted { get; init; } = "（已省略含凭据的文本）";

    /// <summary>按界面当前语言取文案。资源缺失时本地化服务回退内置默认值，不会抛异常。</summary>
    public static BatchReportTexts Current => FromStrings(LocalizationService.Instance.Strings);

    public static BatchReportTexts FromStrings(LanguageStrings strings) => new()
    {
        Title = strings.ReportTitle,
        Headers =
        [
            strings.ReportColumnIndex,
            strings.ReportColumnOperation,
            strings.ReportColumnSource,
            strings.ReportColumnStatus,
            strings.ReportColumnMessage,
            strings.ReportColumnProcessedMB,
            strings.ReportColumnElapsedSeconds
        ],
        Success = strings.ReportStatusSuccess,
        Failed = strings.ReportStatusFailed,
        Skipped = strings.ReportStatusSkipped,
        Compress = strings.ReportOperationCompress,
        Extract = strings.ReportOperationExtract,
        NoteRow = strings.ReportNoteRow,
        SummaryFormat = strings.ReportSummary,
        OmittedNoteFormat = strings.ReportOmittedNote,
        CredentialRedacted = strings.ReportCredentialRedacted
    };

    // 表头数量是 CSV 与 Markdown 共用的列契约，任何语言都必须正好七列。
    public string[] SafeHeaders() =>
        Headers is { Length: 7 } columns ? columns : new BatchReportTexts().Headers;

    public string TranslateStatus(string key) => key switch
    {
        "failed" => Failed,
        "skipped" => Skipped,
        _ => Success
    };

    public string TranslateOperation(string key) => key == "extract" ? Extract : Compress;
}

/// <summary>
/// 从进度快照的计数增量收集逐项结果（与结构化输出器 NDJSON 同一套判定思路）。
/// WorkBuddy, 2026-11-15：批次 20（P4-3）——不需要改动执行管线即可生成历史报告。
/// </summary>
public sealed class BatchReportCollector
{
    /// <summary>含凭据消息的占位标记；写出时由文案包翻译成当前语言。</summary>
    public const string RedactedMarker = "[credential-redacted]";

    /// <summary>单个批次最多保留的条目数；超出只累计计数，避免超大任务把内存吃光。</summary>
    public const int MaxEntries = 20_000;

    private readonly List<BatchReportEntry> _entries = [];
    private readonly object _gate = new();
    private readonly DateTime _startedAt = DateTime.Now;
    private int _omittedEntries;

    private int _successCount;
    private int _failCount;
    private int _postProcessFailCount;
    private int _ignoreCount;
    private int _nonExistCount;
    private int _incompleteVolumeCount;
    private int _ambiguousCount;
    private double _lastProcessedMB;
    private string _operation = "compress";

    /// <summary>
    /// 设置本次批次的类型。存的是语言无关标记，因此可以在任何时刻调用，
    /// 不再受「语言什么时候设置」影响。
    /// </summary>
    public void SetOperation(bool compress) => _operation = compress ? "compress" : "extract";

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

    /// <summary>因超出上限而未列入报告的条目数。</summary>
    public int OmittedEntries
    {
        get
        {
            lock (_gate) return _omittedEntries;
        }
    }

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

            var status = succeeded ? "success" : failed ? "failed" : "skipped";
            // 归档成功但后处理失败时两个计数会同时增加，状态仍按「成功」，
            // 与结构化输出器保持一致，细节留在消息里。
            var currentMB = info.ProcessedSizeGB * 1024.0;
            var deltaMB = Math.Max(0, currentMB - _lastProcessedMB);
            _lastProcessedMB = currentMB;

            // 上限只控制报告明细的容量，计数快照仍照常推进，后续条目只累计省略数。
            if (_entries.Count >= MaxEntries)
            {
                _omittedEntries++;
                return;
            }

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
        return CredentialPattern.IsMatch(flat) ? RedactedMarker : flat;
    }
}

/// <summary>
/// 把逐项结果写成 CSV（逗号分隔值）或 Markdown 汇总表。
/// 只消费 BatchReportEntry 的结构化字段，绝不读取日志文件或命令日志文本。
/// </summary>
public static class BatchReportWriter
{
    public static string ToCsv(
        IReadOnlyList<BatchReportEntry> entries,
        int omittedEntries = 0,
        BatchReportTexts? texts = null)
    {
        texts ??= BatchReportTexts.Current;
        var headers = texts.SafeHeaders();
        var sb = new StringBuilder();
        sb.AppendLine(string.Join(",", headers.Select(CsvCell)));
        for (var i = 0; i < entries.Count; i++)
        {
            var e = entries[i];
            sb.AppendLine(string.Join(",",
                CsvCell((i + 1).ToString(CultureInfo.InvariantCulture)),
                CsvCell(texts.TranslateOperation(e.Operation)),
                CsvCell(e.Source),
                CsvCell(texts.TranslateStatus(e.Status)),
                CsvCell(DisplayMessage(e.Message, texts)),
                CsvCell(e.ProcessedMB.ToString("F3", CultureInfo.InvariantCulture)),
                CsvCell(e.ElapsedSeconds.ToString("F3", CultureInfo.InvariantCulture))));
        }

        if (omittedEntries > 0)
        {
            // 保持列数一致，用一条说明行标注被省略的条目，不破坏表格结构。
            sb.AppendLine(string.Join(",",
                CsvCell(texts.NoteRow),
                CsvCell(string.Format(texts.OmittedNoteFormat, omittedEntries)),
                CsvCell(string.Empty), CsvCell(string.Empty), CsvCell(string.Empty),
                CsvCell(string.Empty), CsvCell(string.Empty)));
        }

        return sb.ToString();
    }

    public static string ToMarkdown(
        IReadOnlyList<BatchReportEntry> entries,
        int omittedEntries = 0,
        BatchReportTexts? texts = null)
    {
        texts ??= BatchReportTexts.Current;
        var headers = texts.SafeHeaders();
        var sb = new StringBuilder();
        sb.Append("# ").AppendLine(texts.Title);
        sb.AppendLine();
        sb.AppendLine("| " + string.Join(" | ", headers) + " |");
        sb.AppendLine("| " + string.Join(" | ", headers.Select(_ => "---")) + " |");
        for (var i = 0; i < entries.Count; i++)
        {
            var e = entries[i];
            sb.AppendLine("| " + string.Join(" | ",
                (i + 1).ToString(CultureInfo.InvariantCulture),
                MdCell(texts.TranslateOperation(e.Operation)),
                MdCell(e.Source),
                MdCell(texts.TranslateStatus(e.Status)),
                MdCell(DisplayMessage(e.Message, texts)),
                e.ProcessedMB.ToString("F3", CultureInfo.InvariantCulture),
                e.ElapsedSeconds.ToString("F3", CultureInfo.InvariantCulture)) + " |");
        }
        sb.AppendLine();
        sb.AppendLine(string.Format(
            texts.SummaryFormat,
            entries.Count,
            entries.Count(e => e.Status == "success"),
            entries.Count(e => e.Status == "failed"),
            entries.Count(e => e.Status == "skipped")));
        if (omittedEntries > 0)
        {
            sb.AppendLine();
            sb.Append("> ").AppendLine(string.Format(texts.OmittedNoteFormat, omittedEntries));
        }

        return sb.ToString();
    }

    /// <summary>
    /// 按扩展名选择格式（.md / .markdown 为 Markdown，其余为 CSV），写入 UTF-8 文件并返回路径。
    /// CSV 带 BOM（字节顺序标记），方便直接用表格软件打开中文表头。
    /// </summary>
    public static string Write(
        string path,
        IReadOnlyList<BatchReportEntry> entries,
        int omittedEntries = 0,
        BatchReportTexts? texts = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        var extension = Path.GetExtension(path);
        var isMarkdown = extension.Equals(".md", StringComparison.OrdinalIgnoreCase) ||
                         extension.Equals(".markdown", StringComparison.OrdinalIgnoreCase);
        texts ??= BatchReportTexts.Current;
        var content = isMarkdown
            ? ToMarkdown(entries, omittedEntries, texts)
            : ToCsv(entries, omittedEntries, texts);

        var fullPath = Path.GetFullPath(path);
        var directory = Path.GetDirectoryName(fullPath);
        if (!string.IsNullOrEmpty(directory))
        {
            Directory.CreateDirectory(directory);
        }

        File.WriteAllText(fullPath, content, new UTF8Encoding(encoderShouldEmitUTF8Identifier: !isMarkdown));
        return fullPath;
    }

    private static string DisplayMessage(string message, BatchReportTexts texts) =>
        message == BatchReportCollector.RedactedMarker ? texts.CredentialRedacted : message;

    /// <summary>
    /// CSV 单元格转义。除双引号外，还中和以 = + - @ 制表符/回车开头的文本，
    /// 防止来源文件名被 Excel / WPS / LibreOffice 当成公式执行。
    /// </summary>
    private static string CsvCell(string? value)
    {
        var safe = (value ?? string.Empty).Replace("\"", "\"\"");
        if (safe.Length > 0 && safe[0] is '=' or '+' or '-' or '@' or '\t' or '\r')
        {
            safe = "'" + safe;
        }

        return $"\"{safe}\"";
    }

    private static string MdCell(string? value) =>
        (value ?? string.Empty).Replace("|", "\\|").Replace("\r", " ").Replace("\n", " ");
}
