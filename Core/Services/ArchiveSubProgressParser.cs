using System;
using System.Text.RegularExpressions;

namespace BatchCompress.Avalonia.Core.Services;

/// <summary>
/// 归档进程输出解析器，用于从 7zz 和 WinRAR 的实时标准输出/错误输出提取分卷序号和进度百分比。
/// Antigravity, 2026-11-08：批次 16（P3-11）——解析 7zz/RAR 输出换算分卷子进度并由 NDJSON/UI 展示。
/// </summary>
public static class ArchiveSubProgressParser
{
    private static readonly Regex PercentageRegex = new(
        @"(?:^|\s)(\d{1,3})%",
        RegexOptions.Compiled);

    private static readonly Regex Volume7zRegex = new(
        @"\.(?:7z|zip)\.(\d{3,})",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);

    private static readonly Regex VolumeRarRegex = new(
        @"\.part(\d+)\.rar",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);

    // WorkBuddy, 2026-11-15：批次 19（P4-1）——只认明确的「数据损坏」类错误，
    // 刻意不认密码错误与普通警告：误判会中断本来能继续解压的归档。
    // 实测 7-Zip 的 CRC（循环冗余校验）失败写的是 stderr 的 "ERROR: CRC Failed : big.bin"。
    private static readonly (Regex Pattern, string Kind)[] FatalErrorPatterns =
    [
        (new Regex(@"\bCRC (?:Failed|Error)\b", RegexOptions.Compiled | RegexOptions.IgnoreCase), "CRC 校验失败"),
        (new Regex(@"\bData Error\b", RegexOptions.Compiled | RegexOptions.IgnoreCase), "数据错误"),
        (new Regex(@"\bUnexpected end of archive\b", RegexOptions.Compiled | RegexOptions.IgnoreCase), "归档意外结束"),
        (new Regex(@"\bCan ?not open (?:the )?file as archive\b", RegexOptions.Compiled | RegexOptions.IgnoreCase), "无法作为归档打开"),
        (new Regex(@"\b(?:Missing volume|Cannot find volume|Can't find volume)\b", RegexOptions.Compiled | RegexOptions.IgnoreCase), "分卷缺失")
    ];

    /// <summary>
    /// 从归档进程的输出行识别致命的数据损坏错误。命中时返回结构化结果，
    /// 供进程边界提前终止当前解压；未命中返回 null。
    /// </summary>
    public static ArchiveFatalError? TryParseFatalError(string? line)
    {
        if (string.IsNullOrWhiteSpace(line)) return null;

        foreach (var (pattern, kind) in FatalErrorPatterns)
        {
            var match = pattern.Match(line);
            if (match.Success)
            {
                return new ArchiveFatalError(kind, match.Value.Trim(), line.Trim());
            }
        }

        return null;
    }

    /// <summary>
    /// 从输出行提取进度百分比（0-100）。若无匹配返回 null。
    /// </summary>
    public static int? TryParsePercentage(string? line)
    {
        if (string.IsNullOrWhiteSpace(line)) return null;

        var match = PercentageRegex.Match(line);
        if (match.Success && int.TryParse(match.Groups[1].Value, out var percent))
        {
            return Math.Clamp(percent, 0, 100);
        }

        return null;
    }

    /// <summary>
    /// 从文件名或输出行提取分卷序号。
    /// </summary>
    public static int? TryParseVolumeNumber(string? pathOrText)
    {
        if (string.IsNullOrWhiteSpace(pathOrText)) return null;

        var m7z = Volume7zRegex.Match(pathOrText);
        if (m7z.Success && int.TryParse(m7z.Groups[1].Value, out var vol7z))
        {
            return vol7z;
        }

        var mRar = VolumeRarRegex.Match(pathOrText);
        if (mRar.Success && int.TryParse(mRar.Groups[1].Value, out var volRar))
        {
            return volRar;
        }

        return null;
    }
}

/// <summary>
/// 归档进程输出里识别到的致命数据损坏错误。
/// WorkBuddy, 2026-11-15：批次 19（P4-1）——Kind 用于分类提示，
/// Detail 是命中的错误短语，SourceLine 是原始输出行（便于日志精确定位到具体文件/分卷）。
/// </summary>
public sealed record ArchiveFatalError(string Kind, string Detail, string SourceLine);

/// <summary>
/// 因输出流中出现致命数据损坏错误而提前终止归档进程时抛出。
/// 与外部用户取消（OperationCanceledException）严格区分：
/// 引擎会把它转换成失败结果，走既有的失败回滚与计数，不会被误当成取消。
/// </summary>
public sealed class ArchiveFatalOutputException : Exception
{
    public ArchiveFatalError Error { get; }

    public ArchiveFatalOutputException(ArchiveFatalError error)
        : base($"归档损坏，已提前终止：{error.Kind} — {error.Detail}（{error.SourceLine}）")
    {
        Error = error;
    }
}
