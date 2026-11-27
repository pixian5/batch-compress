using System;
using System.Collections.Generic;
using System.Text.RegularExpressions;
using BatchCompress.Avalonia.Core.Interfaces;

namespace BatchCompress.Avalonia.Core.Services;

/// <summary>
/// 解压返回警告码 1 时的严重程度分类。
/// </summary>
public enum ExtractionWarningSeverity
{
    None,
    Ignorable,
    Critical
}

/// <summary>
/// 解压警告分级判定结果。
/// </summary>
public sealed record ExtractionWarningAssessment(
    ExtractionWarningSeverity Severity,
    string Reason,
    IReadOnlyList<string> DiagnosticLines);

/// <summary>
/// Gemini 4 Neon, 2026-11-27：0.8.26——基于 7-Zip / RAR 实际进程输出对解压返回码 1 进行三层分级：
/// 1) 重大写入、权限、路径限制或归档错误 → Critical（直接判失败并回滚残留）；
/// 2) 全部诊断行均属于“跨平台属性无法还原 / 轻微元数据异常”白名单 → Ignorable（保留解压结果，按方案 A 仍拦截删除/移动原包）；
/// 3) 空输出或未识别警告 → Critical（保守兜底判失败，避免未知系统报错被静默放行）。
/// </summary>
public static class ExtractionWarningClassifier
{
    private static readonly Regex ProgressPercentOnlyRegex = new(
        @"^\d{1,3}%$",
        RegexOptions.Compiled);

    private static readonly Regex WarningSummaryCountRegex = new(
        @"^(?:Warnings|Archives with Warnings)\s*:\s*\d+$",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);

    private static readonly Regex WarningOrErrorLineRegex = new(
        @"^\s*(?:WARNING|WARN|ERROR)\s*:",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);

    private static readonly Regex ExplicitErrorPrefixRegex = new(
        @"^\s*ERROR\s*:",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);

    private static readonly Regex ErrorSummaryCountRegex = new(
        @"\b(?:Sub items Errors|Files with errors|Archives with Errors)\s*:\s*[1-9]\d*",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);

    private static readonly Regex CriticalWriteOrAccessRegex = new(
        @"(?:\bCan(?:not|'t)\s+(?:open|create|write|delete|rename|remove)\b|\b(?:Permission denied|Access is denied|No space left|Disk full|being used by another process|sharing violation|read-only file system)\b|另一个程序正在使用此文件|拒绝访问|磁盘空间不足|无法创建|无法写入|无法打开)",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);

    private static readonly Regex CriticalPathOrNameRegex = new(
        @"(?:\b(?:File name too long|Path too long|Name too long|The system cannot find the path specified|Illegal byte sequence|Invalid (?:argument|file ?name|path))\b|文件名或扩展名太长|找不到指定的路径)",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);

    private static readonly Regex[] IgnorableWhitelistPatterns =
    [
        new(@"\b(?:Dangerous link path was ignored|Can(?:not|'t)\s+create\s+(?:symbolic|hard)\s+link|symbolic link|symlink|hard\s?link)\b",
            RegexOptions.Compiled | RegexOptions.IgnoreCase),
        new(@"\bCan(?:not|'t)\s+(?:set|restore|change|preserve)\s+(?:file\s+)?(?:attribute|attributes|time|times|timestamp|timestamps|mode|permission|permissions|owner|ownership|group|security|acl|extended attribute|xattr)\b",
            RegexOptions.Compiled | RegexOptions.IgnoreCase),
        new(@"\b(?:Alternative streams?|Alternate data streams?|Zone\.Identifier|extended attributes?)\b",
            RegexOptions.Compiled | RegexOptions.IgnoreCase),
        new(@"\bThere are (?:some )?data after the end of (?:the )?(?:archive|payload data)\b",
            RegexOptions.Compiled | RegexOptions.IgnoreCase),
        new(@"\b(?:Headers? Warning|Trailing data)\b",
            RegexOptions.Compiled | RegexOptions.IgnoreCase)
    ];

    public static ExtractionWarningAssessment Assess(ArchiveResult result)
    {
        ArgumentNullException.ThrowIfNull(result);

        if (!result.HasWarnings)
        {
            return new ExtractionWarningAssessment(ExtractionWarningSeverity.None, string.Empty, []);
        }

        var diagnosticLines = ExtractDiagnosticLines(result.StandardOutput, result.StandardError);
        if (diagnosticLines.Count == 0)
        {
            return new ExtractionWarningAssessment(
                ExtractionWarningSeverity.Critical,
                "未提供可识别的警告详情（保守按失败处理）",
                diagnosticLines);
        }

        // 第 1 层：只要存在任意重大问题行，整次解压直接判失败。
        foreach (var line in diagnosticLines)
        {
            if (ExplicitErrorPrefixRegex.IsMatch(line) || ErrorSummaryCountRegex.IsMatch(line))
            {
                return new ExtractionWarningAssessment(
                    ExtractionWarningSeverity.Critical,
                    "归档解压错误",
                    diagnosticLines);
            }

            if (IsIgnorableLine(line))
            {
                continue;
            }

            if (CriticalPathOrNameRegex.IsMatch(line))
            {
                return new ExtractionWarningAssessment(
                    ExtractionWarningSeverity.Critical,
                    "文件名或路径受限",
                    diagnosticLines);
            }

            if (CriticalWriteOrAccessRegex.IsMatch(line))
            {
                return new ExtractionWarningAssessment(
                    ExtractionWarningSeverity.Critical,
                    "文件写入或权限失败",
                    diagnosticLines);
            }
        }

        // 第 2 层：全部诊断行均命中可忽略白名单才允许放行。
        foreach (var line in diagnosticLines)
        {
            if (!IsIgnorableLine(line))
            {
                // 第 3 层：未知警告一律保守判失败。
                return new ExtractionWarningAssessment(
                    ExtractionWarningSeverity.Critical,
                    "包含未识别警告（保守按失败处理）",
                    diagnosticLines);
            }
        }

        return new ExtractionWarningAssessment(
            ExtractionWarningSeverity.Ignorable,
            "仅含跨平台属性或元数据警告",
            diagnosticLines);
    }

    private static bool IsIgnorableLine(string line)
    {
        if (ExplicitErrorPrefixRegex.IsMatch(line))
        {
            return false;
        }

        foreach (var pattern in IgnorableWhitelistPatterns)
        {
            if (pattern.IsMatch(line))
            {
                return true;
            }
        }

        return false;
    }

    private static IReadOnlyList<string> ExtractDiagnosticLines(string? standardOutput, string? standardError)
    {
        var collected = new List<string>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        void AddLine(string rawLine)
        {
            var trimmed = rawLine.Trim();
            if (trimmed.Length == 0 ||
                trimmed == "--" ||
                ProgressPercentOnlyRegex.IsMatch(trimmed) ||
                WarningSummaryCountRegex.IsMatch(trimmed))
            {
                return;
            }

            if (seen.Add(trimmed))
            {
                collected.Add(trimmed);
            }
        }

        if (!string.IsNullOrWhiteSpace(standardError))
        {
            foreach (var raw in SplitStreamLines(standardError))
            {
                AddLine(raw);
            }
        }

        if (!string.IsNullOrWhiteSpace(standardOutput))
        {
            var inWarningsBlock = false;
            foreach (var raw in SplitStreamLines(standardOutput))
            {
                var trimmed = raw.Trim();
                if (trimmed.Length == 0)
                {
                    inWarningsBlock = false;
                    continue;
                }

                if (trimmed.Equals("WARNINGS:", StringComparison.OrdinalIgnoreCase))
                {
                    inWarningsBlock = true;
                    continue;
                }

                if (inWarningsBlock)
                {
                    if (IsSectionTerminator(trimmed))
                    {
                        inWarningsBlock = false;
                    }
                    else
                    {
                        AddLine(trimmed);
                        continue;
                    }
                }

                if (WarningOrErrorLineRegex.IsMatch(trimmed) ||
                    ErrorSummaryCountRegex.IsMatch(trimmed) ||
                    CriticalPathOrNameRegex.IsMatch(trimmed) ||
                    CriticalWriteOrAccessRegex.IsMatch(trimmed) ||
                    IsIgnorableLine(trimmed))
                {
                    AddLine(trimmed);
                }
            }
        }

        return collected;
    }

    private static IEnumerable<string> SplitStreamLines(string content) =>
        content.Split(['\r', '\n'], StringSplitOptions.None);

    private static bool IsSectionTerminator(string trimmed) =>
        trimmed == "--" ||
        trimmed.StartsWith("Path =", StringComparison.OrdinalIgnoreCase) ||
        trimmed.StartsWith("Type =", StringComparison.OrdinalIgnoreCase) ||
        trimmed.StartsWith("Physical Size =", StringComparison.OrdinalIgnoreCase) ||
        trimmed.StartsWith("Tail Size =", StringComparison.OrdinalIgnoreCase) ||
        trimmed.StartsWith("Headers Size =", StringComparison.OrdinalIgnoreCase) ||
        trimmed.StartsWith("Method =", StringComparison.OrdinalIgnoreCase) ||
        trimmed.StartsWith("Solid =", StringComparison.OrdinalIgnoreCase) ||
        trimmed.StartsWith("Blocks =", StringComparison.OrdinalIgnoreCase) ||
        trimmed.Equals("Everything is Ok", StringComparison.OrdinalIgnoreCase) ||
        trimmed.StartsWith("Archives with ", StringComparison.OrdinalIgnoreCase) ||
        trimmed.StartsWith("Warnings:", StringComparison.OrdinalIgnoreCase) ||
        trimmed.StartsWith("Sub items ", StringComparison.OrdinalIgnoreCase) ||
        trimmed.StartsWith("Files with ", StringComparison.OrdinalIgnoreCase) ||
        trimmed.StartsWith("Size:", StringComparison.OrdinalIgnoreCase) ||
        trimmed.StartsWith("Compressed:", StringComparison.OrdinalIgnoreCase);
}
