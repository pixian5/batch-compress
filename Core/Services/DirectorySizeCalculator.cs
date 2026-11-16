using System;
using System.Collections.Generic;
using System.IO;
using BatchCompress.Avalonia.Localization;

namespace BatchCompress.Avalonia.Core.Services;

/// <summary>目录大小计算结果：成功统计的字节数，以及逐项收集的错误。</summary>
public sealed record DirectorySizeResult(long Size, IReadOnlyList<string> Errors);

// WorkBuddy, 2026-11-17：0.8.15——原实现用 Directory.GetFiles(AllDirectories)，
// 可能跟随目录符号链接形成环路，或把目录外文件算进总量。这里统一跳过重解析点，
// 并把“无权限/读失败”降级为逐项错误，而不是让整个计算失败。
public static class DirectorySizeCalculator
{
    public static DirectorySizeResult Calculate(string path)
    {
        if (!Directory.Exists(path))
        {
            return new DirectorySizeResult(0, Array.Empty<string>());
        }

        long size = 0;
        var errors = new List<string>();
        var options = new EnumerationOptions
        {
            RecurseSubdirectories = true,
            IgnoreInaccessible = true,
            AttributesToSkip = FileAttributes.ReparsePoint,
            ReturnSpecialDirectories = false
        };

        try
        {
            foreach (var file in Directory.EnumerateFiles(path, "*", options))
            {
                if (SystemMetadataFileFilter.ShouldSkip(file))
                {
                    continue;
                }

                try
                {
                    size += new FileInfo(file).Length;
                }
                catch (Exception ex)
                {
                    errors.Add(string.Format(
                        LocalizationService.Instance.Strings.DirectorySizeFileError, file, ex.Message));
                }
            }
        }
        catch (Exception ex)
        {
            errors.Add(string.Format(
                LocalizationService.Instance.Strings.DirectorySizeAccessError, path, ex.Message));
        }

        return new DirectorySizeResult(size, errors);
    }
}
