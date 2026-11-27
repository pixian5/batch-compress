using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using BatchCompress.Avalonia.Core.Models;

namespace BatchCompress.Avalonia.Core.Services;

/// <summary>
/// 从图形界面当前配置生成等效 CLI 命令行参数所需的输入快照。
/// Gemini 4 Hydrogen, 2026-11-27：批次 42（v0.8.31）——不引入 Avalonia 依赖，便于单元测试闭环校验。
/// </summary>
public sealed record CliCommandBuildRequest
{
    public bool IsCompression { get; init; } = true;
    public int SourceMode { get; init; } = 1;
    public string SaveFilePath { get; init; } = string.Empty;
    public string SourcePath { get; init; } = string.Empty;
    public string OutputPath { get; init; } = string.Empty;
    public IReadOnlyList<string> SourceListItems { get; init; } = [];
    public string Extension { get; init; } = "rar";
    public bool UseRandomPassword { get; init; } = true;
    public bool HasCustomPassword { get; init; }
    public int PasswordNameMode { get; init; }
    public int CompressionLevel { get; init; } = ArchiveDefaults.DefaultCompressionLevel;
    public bool SolidArchive { get; init; } = true;
    public bool EnableVolume { get; init; } = ArchiveDefaults.DefaultEnableVolume;
    public string VolumeSize { get; init; } = ArchiveDefaults.DefaultVolumeSize;
    public int VolumeUnit { get; init; }
    public int RecoveryRecordPercent { get; init; } = ArchiveDefaults.DefaultRecoveryRecordPercent;
    public int ExistingFileMode { get; init; } = 2;
    public bool LockArchive { get; init; }
    public bool QuickOpen { get; init; }
    public bool TestArchive { get; init; }
    public bool EnableComment { get; init; } = true;
    public string CommentFilePath { get; init; } = string.Empty;
    public string TempDirectory { get; init; } = string.Empty;
    public bool SkipAlreadyProcessed { get; init; } = true;
    public bool DeleteSourceAfter { get; init; }
    public bool MoveSourceAfter { get; init; }
    public bool StrictExtract { get; init; }
    public double MaxSizeGB { get; init; } = 666;
    public bool ShutdownAfterComplete { get; init; }
    public int MaxConcurrency { get; init; } = 1;
    public bool AddEnclosures { get; init; } = true;
    public IReadOnlyList<string> EnclosureDirectories { get; init; } = [];
    public string? Language { get; init; }
}

/// <summary>
/// 将图形界面选项转换为可复制复跑的等价命令行参数，并与 CommandLineHandler 契约保持一致。
/// 出于凭据安全考虑，明文自定义密码绝不写入生成结果（统一转为 --password-stdin 提示）。
/// </summary>
public static class CliCommandBuilder
{
    public const string DefaultExecutableName = "BatchCompress.Avalonia";
    private const string SourcePlaceholder = "<SOURCE_DIR>";
    private const string OutputPlaceholder = "<OUTPUT_DIR>";
    private const string TextFilePlaceholder = "<PASSWORD_BOOK_TXT>";

    /// <summary>生成可直接传给 CommandLineHandler.ParseArguments 的等价参数数组。</summary>
    public static IReadOnlyList<string> BuildArguments(CliCommandBuildRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);

        var args = new List<string>
        {
            request.IsCompression ? "compress" : "decompress"
        };

        AppendSourceArguments(args, request);

        var output = string.IsNullOrWhiteSpace(request.OutputPath)
            ? OutputPlaceholder
            : request.OutputPath.Trim();
        args.Add("--output");
        args.Add(output);

        var extension = string.IsNullOrWhiteSpace(request.Extension)
            ? "rar"
            : request.Extension.Trim().TrimStart('.');
        args.Add("--extension");
        args.Add(extension);

        if (request.UseRandomPassword)
        {
            if (request.PasswordNameMode == (int)Models.PasswordNameMode.BaseName)
            {
                args.Add("--password-name");
                args.Add("base");
            }
        }
        else
        {
            args.Add("--no-random-password");
            if (request.HasCustomPassword)
            {
                // 安全兜底：绝不在命令行字符串中输出明文密码，改用标准输入标志位。
                args.Add("--password-stdin");
            }
        }

        var existingMode = Math.Clamp(request.ExistingFileMode, 0, 2) switch
        {
            0 => "skip",
            1 => "update",
            _ => "overwrite"
        };
        if (!string.Equals(existingMode, "overwrite", StringComparison.Ordinal))
        {
            args.Add("--existing");
            args.Add(existingMode);
        }

        if (request.IsCompression)
        {
            AppendCompressionArguments(args, request);
        }
        else if (request.StrictExtract)
        {
            args.Add("--strict-extract");
        }

        if (!request.SkipAlreadyProcessed)
        {
            args.Add("--no-skip-processed");
        }

        if (request.MaxConcurrency > 1)
        {
            args.Add("--concurrency");
            args.Add(Math.Clamp(request.MaxConcurrency, 1, 16).ToString(CultureInfo.InvariantCulture));
        }

        if (double.IsFinite(request.MaxSizeGB) && request.MaxSizeGB > 0 && Math.Abs(request.MaxSizeGB - 666) > 0.001)
        {
            args.Add("--max-size");
            args.Add(request.MaxSizeGB.ToString(CultureInfo.InvariantCulture));
        }

        if (request.DeleteSourceAfter)
        {
            args.Add("--delete-source");
        }
        else if (request.MoveSourceAfter)
        {
            args.Add("--move-source");
        }

        if (request.ShutdownAfterComplete)
        {
            args.Add("--shutdown");
        }

        if (!string.IsNullOrWhiteSpace(request.Language))
        {
            args.Add("--language");
            args.Add(request.Language.Trim());
        }

        return args;
    }

    /// <summary>生成可直接在终端执行的完整命令行文本。</summary>
    public static string FormatCommand(CliCommandBuildRequest request, string? executableName = null)
    {
        var exe = string.IsNullOrWhiteSpace(executableName) ? DefaultExecutableName : executableName.Trim();
        var parts = new List<string> { QuoteArgument(exe) };
        parts.AddRange(BuildArguments(request).Select(QuoteArgument));
        return string.Join(' ', parts);
    }

    public static string QuoteArgument(string value)
    {
        if (value.Length == 0)
        {
            return "\"\"";
        }

        var needsQuotes = value.Any(c => char.IsWhiteSpace(c) || c is '"' or '\'' or '$' or '`' or '&' or '|' or ';' or '(' or ')' or '<' or '>');
        if (!needsQuotes)
        {
            return value;
        }

        return $"\"{value.Replace("\"", "\\\"", StringComparison.Ordinal)}\"";
    }

    private static void AppendSourceArguments(List<string> args, CliCommandBuildRequest request)
    {
        var nonEmptyItems = request.SourceListItems
            .Where(item => !string.IsNullOrWhiteSpace(item))
            .Select(item => item.Trim())
            .ToArray();

        if (request.IsCompression && request.SourceMode == 0)
        {
            // CommandLineHandler 规定 --text-file 仅限解压密码本模式；压缩路径清单需展开为 --input。
            if (nonEmptyItems.Length > 0)
            {
                args.Add("--input");
                args.AddRange(nonEmptyItems);
                return;
            }

            args.Add("--input");
            args.Add(string.IsNullOrWhiteSpace(request.SourcePath) ? SourcePlaceholder : request.SourcePath.Trim());
            return;
        }

        if (!request.IsCompression && request.SourceMode == 0)
        {
            if (!string.IsNullOrWhiteSpace(request.SaveFilePath))
            {
                args.Add("--source");
                args.Add(request.SaveFilePath.Trim());
            }

            args.Add("--text-file");
            args.Add(string.IsNullOrWhiteSpace(request.SourcePath) ? TextFilePlaceholder : request.SourcePath.Trim());
            return;
        }

        if (!string.IsNullOrWhiteSpace(request.SaveFilePath))
        {
            args.Add("--source");
            args.Add(request.SaveFilePath.Trim());
            return;
        }

        if (nonEmptyItems.Length > 0)
        {
            args.Add("--input");
            args.AddRange(nonEmptyItems);
            return;
        }

        args.Add("--source");
        args.Add(SourcePlaceholder);
    }

    private static void AppendCompressionArguments(List<string> args, CliCommandBuildRequest request)
    {
        var level = Math.Clamp(request.CompressionLevel, 0, 5);
        args.Add("--level");
        args.Add(level.ToString(CultureInfo.InvariantCulture));

        if (!request.SolidArchive && level > 0)
        {
            args.Add("--no-solid");
        }

        if (request.EnableVolume)
        {
            var volumeSize = string.IsNullOrWhiteSpace(request.VolumeSize)
                ? ArchiveDefaults.DefaultVolumeSize
                : request.VolumeSize.Trim();
            var unit = Math.Clamp(request.VolumeUnit, 0, 2) switch
            {
                1 => "m",
                2 => "k",
                _ => "g"
            };
            args.Add("--volume-size");
            args.Add(volumeSize);
            args.Add("--volume-unit");
            args.Add(unit);
        }

        args.Add("--recovery");
        args.Add(Math.Clamp(request.RecoveryRecordPercent, 0, 100).ToString(CultureInfo.InvariantCulture));

        if (request.LockArchive)
        {
            args.Add("--lock");
        }

        if (request.QuickOpen)
        {
            args.Add("--quick-open");
        }

        if (request.TestArchive)
        {
            args.Add("--test");
        }

        if (request.EnableComment && !string.IsNullOrWhiteSpace(request.CommentFilePath))
        {
            args.Add("--comment");
            args.Add(request.CommentFilePath.Trim());
        }

        if (!string.IsNullOrWhiteSpace(request.TempDirectory) &&
            !VolumeAwarePathComparer.Instance.Equals(
                SourceCollector.NormalizePath(request.TempDirectory),
                SourceCollector.NormalizePath(request.OutputPath)))
        {
            args.Add("--temp-dir");
            args.Add(request.TempDirectory.Trim());
        }

        if (!request.AddEnclosures)
        {
            args.Add("--no-add-enclosures");
        }
        else
        {
            var enclosures = request.EnclosureDirectories
                .Where(dir => !string.IsNullOrWhiteSpace(dir))
                .Select(dir => dir.Trim())
                .ToArray();
            if (enclosures.Length > 0)
            {
                args.Add("--enclosure");
                args.AddRange(enclosures);
            }
        }
    }
}
