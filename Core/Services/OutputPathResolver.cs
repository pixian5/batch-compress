using System;
using System.IO;

namespace BatchCompress.Avalonia.Core.Services;

// GPT-5, 2026-08-05：集中处理输出目录回退，使空输出设置统一使用来源文件父目录，并向调用方返回已存在的绝对目录。
public static class OutputPathResolver
{
    /// <summary>解析输出目录但不创建它，供无副作用预检使用。</summary>
    public static string Resolve(string? configuredPath, string sourcePath) =>
        Path.GetFullPath(string.IsNullOrWhiteSpace(configuredPath)
            ? GetSourceParent(sourcePath)
            : configuredPath);

    public static string ResolveAndCreate(string? configuredPath, string sourcePath)
    {
        var outputDirectory = Resolve(configuredPath, sourcePath);
        Directory.CreateDirectory(outputDirectory);
        return outputDirectory;
    }

    private static string GetSourceParent(string sourcePath)
    {
        var fullSourcePath = Path.GetFullPath(sourcePath);
        return Path.GetDirectoryName(fullSourcePath) ?? Directory.GetCurrentDirectory();
    }
}
