using System;
using System.Collections.Generic;
using System.IO;
using BatchCompress.Avalonia.Core.Models;
using BatchCompress.Avalonia.Core.Services;

namespace BatchCompress.Avalonia.ViewModels;

/// <summary>
/// Gemini 4 Neon, 2026-11-27：批次 38——记录上一次批处理失败的来源路径，支持在界面中一键重载并保持密码本配对格式。
/// </summary>
public sealed class FailedItemRetryList
{
    private static readonly StringComparer PathComparer =
        OperatingSystem.IsLinux() ? StringComparer.Ordinal : StringComparer.OrdinalIgnoreCase;

    private readonly object _gate = new();
    private readonly List<string> _failedPaths = [];
    private readonly HashSet<string> _seen = new(PathComparer);
    private int _failCount;

    public void Record(OperationProgressInfo info)
    {
        if (info == null || info.IsCommandOutput)
        {
            return;
        }

        lock (_gate)
        {
            var failed = info.FailCount > _failCount;
            _failCount = info.FailCount;
            if (!failed || string.IsNullOrWhiteSpace(info.CurrentSourcePath))
            {
                return;
            }

            var path = info.CurrentSourcePath.Trim();
            if (_seen.Add(path))
            {
                _failedPaths.Add(path);
            }
        }
    }

    public IReadOnlyList<string> GetFailedPaths()
    {
        lock (_gate)
        {
            return _failedPaths.ToArray();
        }
    }

    public static string BuildRetrySourceText(
        IReadOnlyList<string> failedPaths,
        string? originalSourceList,
        bool isPasswordBookMode)
    {
        if (failedPaths == null || failedPaths.Count == 0)
        {
            return string.Empty;
        }

        if (!isPasswordBookMode || string.IsNullOrWhiteSpace(originalSourceList))
        {
            return string.Join(Environment.NewLine, failedPaths);
        }

        var passwordByPath = ParsePasswordBookMap(originalSourceList);
        if (passwordByPath.Count == 0)
        {
            return string.Join(Environment.NewLine, failedPaths);
        }

        var lines = new List<string>(failedPaths.Count * 2);
        var matchedAnyPassword = false;
        foreach (var path in failedPaths)
        {
            lines.Add(path);
            if (TryLookupPassword(passwordByPath, path, out var password))
            {
                lines.Add(password);
                matchedAnyPassword = true;
            }
        }

        return matchedAnyPassword
            ? string.Join(Environment.NewLine, lines)
            : string.Join(Environment.NewLine, failedPaths);
    }

    private static Dictionary<string, string> ParsePasswordBookMap(string originalSourceList)
    {
        var map = new Dictionary<string, string>(PathComparer);
        var nonEmptyLines = SourceCollector.SplitLines(originalSourceList);
        for (var i = 0; i + 1 < nonEmptyLines.Count; i += 2)
        {
            var rawPath = nonEmptyLines[i].Trim();
            var password = nonEmptyLines[i + 1];
            map.TryAdd(rawPath, password);

            var normalized = NormalizeSafe(rawPath);
            if (!string.IsNullOrEmpty(normalized))
            {
                map.TryAdd(normalized, password);
            }
        }

        return map;
    }

    private static bool TryLookupPassword(
        IReadOnlyDictionary<string, string> passwordByPath,
        string path,
        out string password)
    {
        if (passwordByPath.TryGetValue(path, out password!))
        {
            return true;
        }

        var normalized = NormalizeSafe(path);
        if (!string.IsNullOrEmpty(normalized) && passwordByPath.TryGetValue(normalized, out password!))
        {
            return true;
        }

        password = string.Empty;
        return false;
    }

    private static string NormalizeSafe(string path)
    {
        try
        {
            return Path.GetFullPath(path);
        }
        catch
        {
            return path.Trim();
        }
    }
}
