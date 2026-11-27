using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
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
        var groups = outputPlans.GroupBy(
            plan => GetCollisionIdentity(plan.TargetPath, !string.IsNullOrWhiteSpace(volumeSize)),
            VolumeAwarePathComparer.Instance);

        foreach (var group in groups.Where(group => group.Count() > 1))
        {
            var plans = group.ToArray();
            var targetComparer = FileSystemPathIdentity.GetComparer(plans[0].TargetPath);
            issues.Add(new BatchPreflightIssue(
                BatchPreflightIssueKind.OutputCollision,
                plans[0].TargetPath,
                plans.Select(plan => plan.SourcePath).Distinct(targetComparer).ToArray()));
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

/// <summary>按各路径所在卷动态选用大小写比较器的比较器实现。</summary>
internal sealed class VolumeAwarePathComparer : IEqualityComparer<string>
{
    public static readonly VolumeAwarePathComparer Instance = new();

    public bool Equals(string? x, string? y)
    {
        if (ReferenceEquals(x, y)) return true;
        if (x is null || y is null) return false;
        return FileSystemPathIdentity.GetComparer(x).Equals(x, y);
    }

    public int GetHashCode(string obj) =>
        obj is null ? 0 : FileSystemPathIdentity.GetComparer(obj).GetHashCode(obj);
}

/// <summary>跨平台路径身份辅助方法，支持按卷检测大小写敏感能力与符号链接解析。</summary>
internal static class FileSystemPathIdentity
{
    // POSIX macOS pathconf(_PC_CASE_SENSITIVE) = 11 (/usr/include/sys/unistd.h)
    private const int MacOsPathConfCaseSensitive = 11;
    private const int MaxCacheEntries = 256;
    private static readonly ConcurrentDictionary<string, bool> CaseSensitivityCache = new(StringComparer.Ordinal);

    [DllImport("libSystem.dylib", EntryPoint = "pathconf", SetLastError = true)]
    private static extern nint MacOsPathConf(string path, int name);

    public static bool IsCaseSensitive(string? path)
    {
        var probeDir = FindNearestExistingDirectory(path);
        if (probeDir == null)
        {
            return GetPlatformDefaultCaseSensitivity();
        }

        if (CaseSensitivityCache.TryGetValue(probeDir, out var cached))
        {
            return cached;
        }

        var detected = DetectCaseSensitivityForDirectory(probeDir);
        if (CaseSensitivityCache.Count >= MaxCacheEntries)
        {
            CaseSensitivityCache.Clear();
        }

        CaseSensitivityCache[probeDir] = detected;
        return detected;
    }

    public static StringComparer GetComparer(string path) =>
        IsCaseSensitive(path) ? StringComparer.Ordinal : StringComparer.OrdinalIgnoreCase;

    private static bool GetPlatformDefaultCaseSensitivity() =>
        !OperatingSystem.IsWindows() && !OperatingSystem.IsMacOS();

    private static string? FindNearestExistingDirectory(string? path)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return null;
        }

        try
        {
            var current = Path.GetFullPath(path);
            if (File.Exists(current))
            {
                current = Path.GetDirectoryName(current);
            }

            while (!string.IsNullOrEmpty(current))
            {
                if (Directory.Exists(current))
                {
                    return current;
                }

                var parent = Path.GetDirectoryName(current);
                if (string.Equals(parent, current, StringComparison.Ordinal))
                {
                    break;
                }

                current = parent;
            }
        }
        catch
        {
            return null;
        }

        return null;
    }

    private static bool DetectCaseSensitivityForDirectory(string probeDir)
    {
        if (OperatingSystem.IsMacOS())
        {
            try
            {
                var value = MacOsPathConf(probeDir, MacOsPathConfCaseSensitive);
                if (value == 1)
                {
                    return true;
                }

                if (value == 0)
                {
                    return false;
                }
            }
            catch
            {
                // P/Invoke 不可用时安全回退默认策略。
            }

            return false;
        }

        if (OperatingSystem.IsWindows())
        {
            return false;
        }

        return ProbeUnixDirectoryCaseSensitivity(probeDir);
    }

    private static bool ProbeUnixDirectoryCaseSensitivity(string probeDir)
    {
        try
        {
            var entries = Directory.EnumerateFileSystemEntries(probeDir).Take(64).ToArray();
            var literalNames = entries
                .Select(Path.GetFileName)
                .Where(name => !string.IsNullOrEmpty(name))
                .ToHashSet(StringComparer.Ordinal);

            foreach (var entryPath in entries)
            {
                var name = Path.GetFileName(entryPath);
                if (string.IsNullOrEmpty(name) || !TryFlipFirstAsciiLetter(name, out var flippedName))
                {
                    continue;
                }

                // 目录内若本来就同时存在该大小写变体，无法单独用来判断，继续看下一项。
                if (literalNames.Contains(flippedName))
                {
                    return true;
                }

                var flippedPath = Path.Combine(probeDir, flippedName);
                return !File.Exists(flippedPath) && !Directory.Exists(flippedPath);
            }
        }
        catch
        {
            // 枚举失败时回退 Linux 默认大小写敏感。
        }

        return true;
    }

    private static bool TryFlipFirstAsciiLetter(string name, out string flipped)
    {
        var chars = name.ToCharArray();
        for (var i = 0; i < chars.Length; i++)
        {
            var ch = chars[i];
            if (ch is >= 'a' and <= 'z')
            {
                chars[i] = char.ToUpperInvariant(ch);
                flipped = new string(chars);
                return true;
            }

            if (ch is >= 'A' and <= 'Z')
            {
                chars[i] = char.ToLowerInvariant(ch);
                flipped = new string(chars);
                return true;
            }
        }

        flipped = string.Empty;
        return false;
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
        var comparison = IsCaseSensitive(volumePath)
            ? StringComparison.Ordinal
            : StringComparison.OrdinalIgnoreCase;
        return candidate.StartsWith(prefix, comparison);
    }

    public static bool IsStrictDescendant(string candidate, string ancestor, string volumePath) =>
        !GetComparer(volumePath).Equals(candidate, ancestor) && IsSameOrDescendant(candidate, ancestor, volumePath);
}
