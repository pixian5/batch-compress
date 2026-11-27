using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using BatchCompress.Avalonia.Core.Models;
using BatchCompress.Avalonia.Localization;

namespace BatchCompress.Avalonia.Core.Services;

/// <summary>
/// 图形界面无副作用预检结果快照。
/// </summary>
public sealed record GuiPreflightSummary(
    bool CanExecute,
    int ReadyCount,
    int SkippedCount,
    int MissingCount,
    int IssueCount,
    string SummaryText);

/// <summary>
/// 为图形界面提供只读“预检 / 试运行”能力：零磁盘写入、零引擎进程调用、零联网安装副作用。
/// Gemini 4 Hydrogen, 2026-11-27：批次 44（v0.8.34）。
/// </summary>
public static class GuiPreflightService
{
    private const int MaxListedPaths = 10;
    private static LanguageStrings L => LocalizationService.Instance.Strings;

    public static GuiPreflightSummary InspectCompression(
        IEnumerable<string>? rawSourcePaths,
        BatchOperationOptions options,
        int recommendedConcurrency,
        bool hasUpdateLockConflict)
    {
        ArgumentNullException.ThrowIfNull(options);

        var collected = (rawSourcePaths ?? [])
            .Select(SourceCollector.NormalizePath)
            .Where(path => path.Length > 0 && !SystemMetadataFileFilter.ShouldSkip(path))
            .Distinct(VolumeAwarePathComparer.Instance)
            .ToArray();

        var issueLines = new List<string>();
        var normalizedFormat = ArchiveFormatCatalog.Normalize(options.Extension);
        if (!ArchiveFormatCatalog.TryGet(normalizedFormat, out var formatDef) || !formatDef.CanCreate)
        {
            issueLines.Add(string.Format(L.FormatCannotCreate, options.Extension, ArchiveFormatCatalog.CreateFormatsText));
        }

        if (hasUpdateLockConflict)
        {
            issueLines.Add(L.UpdateConflictsLockArchive);
        }

        if (string.IsNullOrWhiteSpace(options.OutputPath))
        {
            issueLines.Add(L.CliRunnerOutputRequired);
        }

        var missingPaths = new List<string>();
        var skippedCount = 0;
        var readyCount = 0;

        var preflight = BatchPreflightService.InspectCompression(collected, options);
        foreach (var issue in preflight.Issues)
        {
            issueLines.Add(FormatCompressionIssue(issue));
        }

        var conflictedSources = preflight.Issues
            .SelectMany(issue => issue.SourcePaths)
            .ToHashSet(VolumeAwarePathComparer.Instance);

        foreach (var path in preflight.SourcePaths)
        {
            if (!File.Exists(path) && !Directory.Exists(path))
            {
                missingPaths.Add(path);
                continue;
            }

            if (conflictedSources.Contains(path))
            {
                continue;
            }

            var name = Path.GetFileName(path);
            if (options.SkipAlreadyProcessed && name.Contains("【已压缩】", StringComparison.Ordinal))
            {
                skippedCount++;
                continue;
            }

            if (options.ExistingFileMode == Core.Interfaces.ExistingFileMode.Skip &&
                !string.IsNullOrWhiteSpace(options.OutputPath))
            {
                var target = Pipeline.CompressionItemAdapter.BuildOutputPath(
                    options.OutputPath, path, normalizedFormat);
                if (ArchiveVolumeResolver.ResolveOutputFiles(target).Count > 0)
                {
                    skippedCount++;
                    continue;
                }
            }

            readyCount++;
        }

        var issueCount = issueLines.Count;
        var canExecute = issueCount == 0 && readyCount > 0;
        var summaryText = BuildSummaryText(
            canExecute, readyCount, skippedCount, missingPaths, issueLines,
            options.MaxConcurrency, recommendedConcurrency);

        return new GuiPreflightSummary(canExecute, readyCount, skippedCount, missingPaths.Count, issueCount, summaryText);
    }

    public static GuiPreflightSummary InspectDecompression(
        IEnumerable<FileEntry>? rawEntries,
        BatchOperationOptions options,
        int recommendedConcurrency)
    {
        ArgumentNullException.ThrowIfNull(options);

        var issueLines = new List<string>();
        if (string.IsNullOrWhiteSpace(options.OutputPath))
        {
            issueLines.Add(L.CliRunnerOutputRequired);
        }

        var missingPaths = new List<string>();
        var seenArchives = new HashSet<string>(VolumeAwarePathComparer.Instance);
        var skippedCount = 0;
        var readyCount = 0;

        foreach (var entry in rawEntries ?? [])
        {
            if (string.IsNullOrWhiteSpace(entry.FilePath) || SystemMetadataFileFilter.ShouldSkip(entry.FilePath))
            {
                continue;
            }

            var inspection = BatchPreflightService.InspectDecompressionPath(entry.FilePath);
            if (inspection.Status == DecompressionPathStatus.Missing)
            {
                missingPaths.Add(entry.FilePath);
                continue;
            }

            if (inspection.Status != DecompressionPathStatus.Valid || inspection.ArchivePath is null)
            {
                issueLines.Add(BatchOperationService.FormatDecompressionIssue(inspection, entry.FilePath));
                continue;
            }

            var archivePath = inspection.ArchivePath;

            if (!seenArchives.Add(Path.GetFullPath(archivePath)))
            {
                skippedCount++;
                continue;
            }

            if (options.SkipAlreadyProcessed && Path.GetFileName(archivePath).Contains("【已解压】", StringComparison.Ordinal))
            {
                skippedCount++;
                continue;
            }

            readyCount++;
        }

        var issueCount = issueLines.Count;
        var canExecute = issueCount == 0 && readyCount > 0;
        var summaryText = BuildSummaryText(
            canExecute, readyCount, skippedCount, missingPaths, issueLines,
            options.MaxConcurrency, recommendedConcurrency);

        return new GuiPreflightSummary(canExecute, readyCount, skippedCount, missingPaths.Count, issueCount, summaryText);
    }

    private static string FormatCompressionIssue(BatchPreflightIssue issue)
    {
        var sources = string.Join(", ", issue.SourcePaths);
        return issue.Kind switch
        {
            BatchPreflightIssueKind.OutputCollision =>
                string.Format(L.BatchOutputCollision, issue.TargetPath, sources),
            BatchPreflightIssueKind.OutputInsideSource =>
                string.Format(L.BatchOutputInsideSource, issue.SourcePaths.FirstOrDefault() ?? string.Empty, issue.TargetPath),
            BatchPreflightIssueKind.OutputMatchesSource =>
                string.Format(L.BatchOutputMatchesSource, issue.TargetPath, sources),
            _ => issue.TargetPath
        };
    }

    private static string BuildSummaryText(
        bool canExecute,
        int readyCount,
        int skippedCount,
        IReadOnlyList<string> missingPaths,
        IReadOnlyList<string> issueLines,
        int currentConcurrency,
        int recommendedConcurrency)
    {
        var lines = new List<string>();
        if (issueLines.Count > 0 || missingPaths.Count > 0 && readyCount == 0)
        {
            lines.Add(L.PreflightBlockedHeader);
        }
        else if (readyCount == 0)
        {
            lines.Add(L.PreflightNoItemsHeader);
        }
        else
        {
            lines.Add(L.PreflightPassedHeader);
        }

        lines.Add(string.Format(
            L.PreflightSummaryStats,
            readyCount,
            skippedCount,
            missingPaths.Count,
            issueLines.Count,
            Math.Clamp(currentConcurrency, 1, 16),
            recommendedConcurrency));

        if (issueLines.Count > 0)
        {
            lines.Add(string.Empty);
            lines.Add(string.Format(L.PreflightIssueHeader, issueLines.Count));
            foreach (var issue in issueLines.Take(MaxListedPaths))
            {
                lines.Add($"  - {issue}");
            }
        }

        if (missingPaths.Count > 0)
        {
            lines.Add(string.Empty);
            lines.Add(string.Format(L.PreflightMissingHeader, missingPaths.Count));
            foreach (var missing in missingPaths.Take(MaxListedPaths))
            {
                lines.Add($"  - {missing}");
            }
        }

        return string.Join(Environment.NewLine, lines);
    }
}
