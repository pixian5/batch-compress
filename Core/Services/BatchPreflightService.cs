using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using BatchCompress.Avalonia.Core.Models;
using BatchCompress.Avalonia.Core.Services.Pipeline;

namespace BatchCompress.Avalonia.Core.Services;

/// <summary>压缩批次在执行前发现的阻塞问题。</summary>
public enum BatchPreflightIssueKind
{
    OutputCollision,
    OutputInsideSource,
    OutputMatchesSource
}

/// <summary>一条阻塞问题及其涉及的路径。</summary>
public sealed record BatchPreflightIssue(
    BatchPreflightIssueKind Kind,
    string TargetPath,
    IReadOnlyList<string> SourcePaths);

/// <summary>压缩批次预检结果；预检阶段不会创建目录或写入文件。</summary>
public sealed class BatchPreflightResult
{
    public IReadOnlyList<string> SourcePaths { get; init; } = [];
    public IReadOnlyList<BatchPreflightIssue> Issues { get; init; } = [];
    public bool CanExecute => Issues.Count == 0;
}

/// <summary>
/// 压缩批次执行前统一检查输出目标，避免同名覆盖、输出递归进入来源和覆盖来源文件。
/// </summary>
public static class BatchPreflightService
{
    public static BatchPreflightResult InspectCompression(
        IEnumerable<string>? sourcePaths,
        BatchOperationOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);

        var normalizedSources = (sourcePaths ?? [])
            .Select(SourceCollector.NormalizePath)
            .Where(path => path.Length > 0)
            .ToList();
        var includedSources = FilterCollectionOutputDirectory(normalizedSources, options);
        var issues = new List<BatchPreflightIssue>();
        var outputPlans = new List<OutputPlan>();
        var existingSources = includedSources
            .Where(path => File.Exists(path) || Directory.Exists(path))
            .Where(path => !SystemMetadataFileFilter.ShouldSkip(path))
            .ToArray();

        foreach (var sourcePath in existingSources)
        {
            var name = Path.GetFileName(sourcePath);
            if (options.SkipAlreadyProcessed && name.Contains("【已压缩】", StringComparison.Ordinal))
            {
                continue;
            }

            var outputDirectory = OutputPathResolver.Resolve(options.OutputPath, sourcePath);
            var resolvedOutputDirectory = FileSystemPathIdentity.ResolveBestEffort(outputDirectory);
            if (Directory.Exists(sourcePath))
            {
                var resolvedSourceDirectory = FileSystemPathIdentity.ResolveBestEffort(sourcePath);
                if (FileSystemPathIdentity.IsSameOrDescendant(
                        resolvedOutputDirectory, resolvedSourceDirectory, outputDirectory))
                {
                    issues.Add(new BatchPreflightIssue(
                        BatchPreflightIssueKind.OutputInsideSource,
                        outputDirectory,
                        [sourcePath]));
                    continue;
                }
            }

            var targetPath = CompressionItemAdapter.BuildOutputPath(
                options.OutputPath,
                sourcePath,
                ArchiveFormatCatalog.Normalize(options.Extension));
            outputPlans.Add(new OutputPlan(sourcePath, targetPath));
        }

        AddOutputSourceConflicts(outputPlans, existingSources, issues);
        AddDuplicateOutputConflicts(outputPlans, options.VolumeSize, issues);

        return new BatchPreflightResult
        {
            SourcePaths = includedSources,
            Issues = CoalesceIssues(issues)
        };
    }

    /// <summary>
    /// 从目录直接子项中排除作为输出目录的那个子目录；输出若嵌在其它待压缩目录中，仍会被阻止。
    /// </summary>
    public static bool ShouldExcludeCollectionEntry(
        string collectionRoot,
        string entryPath,
        string? outputPath)
    {
        if (string.IsNullOrWhiteSpace(outputPath))
        {
            return false;
        }

        var lexicalRoot = Path.GetFullPath(collectionRoot);
        var lexicalEntry = Path.GetFullPath(entryPath);
        var lexicalOutput = Path.GetFullPath(outputPath);
        if (IsCollectionOutputEntry(lexicalRoot, lexicalEntry, lexicalOutput))
        {
            return true;
        }

        // 同时比较真实路径，处理集合根目录本身经符号链接访问的情况；保留字面路径判断，
        // 才能排除指向集合外部的输出目录符号链接，避免把该链接误当成压缩来源。
        var resolvedRoot = FileSystemPathIdentity.ResolveBestEffort(lexicalRoot);
        var resolvedEntry = FileSystemPathIdentity.ResolveBestEffort(lexicalEntry);
        var resolvedOutput = FileSystemPathIdentity.ResolveBestEffort(lexicalOutput);
        return IsCollectionOutputEntry(resolvedRoot, resolvedEntry, resolvedOutput);
    }

    private static bool IsCollectionOutputEntry(string root, string entry, string output)
    {
        var comparer = FileSystemPathIdentity.GetComparer(root);
        if (comparer.Equals(output, root) ||
            !FileSystemPathIdentity.IsStrictDescendant(output, root, root))
        {
            return false;
        }

        // 只排除集合根目录下与输出目录相同的直接子项；若输出藏在另一个待压缩目录里，
        // 不移除该来源，后面的重叠检查会阻止整批执行。
        var entryParent = Path.GetDirectoryName(entry);
        return comparer.Equals(entryParent, root) && comparer.Equals(entry, output);
    }

    private static List<string> FilterCollectionOutputDirectory(
        List<string> sourcePaths,
        BatchOperationOptions options)
    {
        if (string.IsNullOrWhiteSpace(options.SourceCollectionRoot) ||
            string.IsNullOrWhiteSpace(options.OutputPath))
        {
            return sourcePaths;
        }

        return sourcePaths
            .Where(path => !ShouldExcludeCollectionEntry(
                options.SourceCollectionRoot, path, options.OutputPath))
            .ToList();
    }

    private static void AddOutputSourceConflicts(
        IReadOnlyList<OutputPlan> outputPlans,
        IReadOnlyList<string> sourcePaths,
        ICollection<BatchPreflightIssue> issues)
    {
        foreach (var plan in outputPlans)
        {
            var outputIdentity = FileSystemPathIdentity.ResolveBestEffort(plan.TargetPath);
            var comparer = FileSystemPathIdentity.GetComparer(outputIdentity);
            foreach (var sourcePath in sourcePaths)
            {
                var sourceIdentity = FileSystemPathIdentity.ResolveBestEffort(sourcePath);
                if (!comparer.Equals(outputIdentity, sourceIdentity))
                {
                    continue;
                }

                issues.Add(new BatchPreflightIssue(
                    BatchPreflightIssueKind.OutputMatchesSource,
                    plan.TargetPath,
                    [plan.SourcePath, sourcePath]));
            }
        }
    }

    private static void AddDuplicateOutputConflicts(
        IReadOnlyList<OutputPlan> outputPlans,
        string? volumeSize,
        ICollection<BatchPreflightIssue> issues)
    {
        var comparer = FileSystemPathIdentity.GetComparer(Directory.GetCurrentDirectory());
        var groups = outputPlans.GroupBy(
            plan => GetCollisionIdentity(plan.TargetPath, !string.IsNullOrWhiteSpace(volumeSize)),
            comparer);

        foreach (var group in groups.Where(group => group.Count() > 1))
        {
            var plans = group.ToArray();
            issues.Add(new BatchPreflightIssue(
                BatchPreflightIssueKind.OutputCollision,
                plans[0].TargetPath,
                plans.Select(plan => plan.SourcePath).Distinct(comparer).ToArray()));
        }
    }

    private static string GetCollisionIdentity(string targetPath, bool volumeEnabled)
    {
        var resolvedTarget = FileSystemPathIdentity.ResolveBestEffort(targetPath);
        if (!volumeEnabled)
        {
            return resolvedTarget;
        }

        var directory = Path.GetDirectoryName(resolvedTarget) ?? Directory.GetCurrentDirectory();
        var logicalName = ArchiveVolumeResolver.GetLogicalArchiveName(resolvedTarget);
        return Path.Combine(directory, logicalName);
    }

    private static IReadOnlyList<BatchPreflightIssue> CoalesceIssues(IEnumerable<BatchPreflightIssue> issues) =>
        issues
            .GroupBy(issue => (issue.Kind, issue.TargetPath), new IssueKeyComparer())
            .Select(group => new BatchPreflightIssue(
                group.Key.Kind,
                group.Key.TargetPath,
                group.SelectMany(issue => issue.SourcePaths)
                    .Distinct(FileSystemPathIdentity.GetComparer(group.Key.TargetPath))
                    .ToArray()))
            .ToArray();

    private sealed record OutputPlan(string SourcePath, string TargetPath);

    private sealed class IssueKeyComparer : IEqualityComparer<(BatchPreflightIssueKind Kind, string TargetPath)>
    {
        public bool Equals(
            (BatchPreflightIssueKind Kind, string TargetPath) x,
            (BatchPreflightIssueKind Kind, string TargetPath) y) =>
            x.Kind == y.Kind && FileSystemPathIdentity.GetComparer(x.TargetPath).Equals(x.TargetPath, y.TargetPath);

        public int GetHashCode((BatchPreflightIssueKind Kind, string TargetPath) value) =>
            HashCode.Combine(value.Kind, FileSystemPathIdentity.GetComparer(value.TargetPath).GetHashCode(value.TargetPath));
    }
}

/// <summary>跨平台路径身份辅助方法，尽量解析已有符号链接后再做包含关系判断。</summary>
internal static class FileSystemPathIdentity
{
    public static StringComparer GetComparer(string path)
    {
        // Windows 与 macOS 默认卷按忽略大小写处理；Linux 保留大小写区别。
        // 来源收集仍使用 Ordinal，确保大小写不同的来源不会被静默丢弃。
        _ = path;
        return OperatingSystem.IsWindows() || OperatingSystem.IsMacOS()
            ? StringComparer.OrdinalIgnoreCase
            : StringComparer.Ordinal;
    }

    public static string ResolveBestEffort(string path)
    {
        var fullPath = Path.GetFullPath(path);
        var root = Path.GetPathRoot(fullPath) ?? string.Empty;
        var parts = fullPath[root.Length..]
            .Split([Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar], StringSplitOptions.RemoveEmptyEntries);
        var current = root;
        var existingPrefix = true;

        foreach (var part in parts)
        {
            current = Path.Combine(current, part);
            if (!existingPrefix)
            {
                continue;
            }

            try
            {
                if (Directory.Exists(current))
                {
                    var target = new DirectoryInfo(current).ResolveLinkTarget(returnFinalTarget: true);
                    if (target != null)
                    {
                        current = Path.GetFullPath(target.FullName);
                    }
                }
                else if (File.Exists(current))
                {
                    var target = new FileInfo(current).ResolveLinkTarget(returnFinalTarget: true);
                    if (target != null)
                    {
                        current = Path.GetFullPath(target.FullName);
                    }
                    else
                    {
                        existingPrefix = false;
                    }
                }
                else
                {
                    existingPrefix = false;
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or NotSupportedException)
            {
                existingPrefix = false;
            }
        }

        return Path.GetFullPath(current);
    }

    public static bool IsSameOrDescendant(string candidate, string ancestor, string volumePath)
    {
        var comparer = GetComparer(volumePath);
        if (comparer.Equals(candidate, ancestor))
        {
            return true;
        }

        var prefix = ancestor.EndsWith(Path.DirectorySeparatorChar)
            ? ancestor
            : ancestor + Path.DirectorySeparatorChar;
        var comparison = OperatingSystem.IsWindows() || OperatingSystem.IsMacOS()
            ? StringComparison.OrdinalIgnoreCase
            : StringComparison.Ordinal;
        return candidate.StartsWith(prefix, comparison);
    }

    public static bool IsStrictDescendant(string candidate, string ancestor, string volumePath) =>
        !GetComparer(volumePath).Equals(candidate, ancestor) && IsSameOrDescendant(candidate, ancestor, volumePath);
}
