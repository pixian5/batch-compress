using System;
using System.Collections.Generic;

namespace BatchCompress.Avalonia.ViewModels;

/// <summary>
/// Gemini 4 Neon, 2026-11-26：批次 36——界面日志实时搜索纯逻辑过滤器，不依赖 Avalonia 控件，便于单元测试。
/// </summary>
public static class LogSearchFilter
{
    public static string FilterLines(string? text, string? query)
    {
        if (string.IsNullOrEmpty(text))
        {
            return string.Empty;
        }

        if (string.IsNullOrWhiteSpace(query))
        {
            return text;
        }

        var keyword = query.Trim();
        var lines = text.Split(["\r\n", "\n"], StringSplitOptions.None);
        var matched = new List<string>();
        foreach (var line in lines)
        {
            if (line.Contains(keyword, StringComparison.OrdinalIgnoreCase))
            {
                matched.Add(line);
            }
        }

        return matched.Count == 0 ? string.Empty : string.Join(Environment.NewLine, matched);
    }
}
