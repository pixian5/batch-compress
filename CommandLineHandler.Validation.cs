using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using BatchCompress.Avalonia.Core.Models;

namespace BatchCompress.Avalonia;

// Gemini 4 Hydrogen, 2026-11-27：批次 45（v0.8.35）——从 CommandLineHandler.cs 拆出参数规范化、
// 语言预扫描与合法性校验逻辑，降低主解析器体积。
public static partial class CommandLineHandler
{
    // GLM-5.3, 2026-11-04：批次 7——“纯 GUI 平滑启动”判定：
    // 仅当用户显式给出有效 --language/--lang，且未携带任何批处理意图
    // （--input/--source/--text-file/--output 均未出现）时才默认进入 GUI；
    // 携带批处理来源或输出的命令行仍必须显式指定模式，避免误启动 GUI 掩盖参数错误。
    private static bool ShouldDefaultToGui(CommandLineOptions options, bool languageRequested)
    {
        return languageRequested &&
               string.IsNullOrWhiteSpace(options.SourcePath) &&
               string.IsNullOrWhiteSpace(options.OutputPath) &&
               string.IsNullOrWhiteSpace(options.TextFile) &&
               options.InputPaths.Length == 0;
    }

    /// <summary>
    /// 在完整解析前从原始参数预扫描 --language/--lang（支持 “--lang en” 与 “--lang=en” 两种形式），
    /// 返回归一化语言键；未指定或值无效时返回 null。供 --help 等不经过完整解析的路径使用。
    /// </summary>
    public static string? DetectLanguage(string[] args)
    {
        ArgumentNullException.ThrowIfNull(args);
        for (var index = 0; index < args.Length; index++)
        {
            var token = args[index];
            if (!token.StartsWith("--", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            var equalsIndex = token.IndexOf('=');
            var name = (equalsIndex < 0 ? token : token[..equalsIndex]).Trim();
            if (!name.Equals("--language", StringComparison.OrdinalIgnoreCase) &&
                !name.Equals("--lang", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            var value = equalsIndex < 0
                ? index + 1 < args.Length ? args[index + 1] : null
                : token[(equalsIndex + 1)..];
            if (string.IsNullOrWhiteSpace(value))
            {
                return null;
            }

            // 预扫描不做错误报告：无效值按“未指定”处理，回退默认语言。
            return TryNormalizeLanguage(value, out var language) ? language : null;
        }

        return null;
    }

    // GLM-5.3, 2026-11-04：批次 3——接受 BCP-47 区域变体并归一到 LocalizationService 的语言键。
    private static string NormalizeLanguage(string value, List<string>? errors)
    {
        if (TryNormalizeLanguage(value, out var language))
        {
            return language;
        }

        errors?.Add(string.Format(L.CliErrorLanguageChoices, value));
        return value.Trim();
    }

    private static bool TryNormalizeLanguage(string value, out string language)
    {
        var normalized = (value ?? string.Empty).Trim().ToLowerInvariant();
        switch (normalized)
        {
            case "zh" or "zh-cn" or "zh-hans":
                language = "zh-CN";
                return true;
            case "zh-tw" or "zh-hant" or "tw":
                language = "zh-TW";
                return true;
            case "en" or "en-us":
                language = "en";
                return true;
            case "ja" or "ja-jp":
                language = "ja";
                return true;
            case "de" or "de-de":
                language = "de";
                return true;
            default:
                language = string.Empty;
                return false;
        }
    }

    private static string NormalizeOutputFormat(string value, List<string>? errors)
    {
        var normalized = (value ?? string.Empty).Trim().ToLowerInvariant();
        if (normalized is "text" or "json")
        {
            return normalized;
        }

        errors?.Add(string.Format(L.CliErrorOutputFormatChoices, value));
        return "text";
    }

    private static void Validate(
        CommandLineOptions options,
        bool explicitGui,
        bool languageRequested,
        int explicitPasswordSourceCount,
        string[] effectiveArgs,
        List<string> errors)
    {
        if (options.Compress && options.Decompress)
        {
            errors.Add(L.CliErrorBothModes);
        }

        if (explicitGui && (options.Compress || options.Decompress))
        {
            errors.Add(L.CliErrorGuiWithMode);
        }

        var isHeadless = options.Compress || options.Decompress || !string.IsNullOrWhiteSpace(options.ExportDiagnosticsPath);
        if (!isHeadless && !explicitGui && effectiveArgs.Length > 0 &&
            !ShouldDefaultToGui(options, languageRequested))
        {
            errors.Add(L.CliErrorSpecifyMode);
        }

        if (!isHeadless)
        {
            return;
        }

        options.Gui = false;
        if (!string.IsNullOrWhiteSpace(options.ExportDiagnosticsPath))
        {
            return;
        }

        if (string.IsNullOrWhiteSpace(options.OutputPath))
        {
            errors.Add(L.CliErrorOutputRequired);
        }

        if (string.IsNullOrWhiteSpace(options.SourcePath) &&
            options.InputPaths.Length == 0 &&
            string.IsNullOrWhiteSpace(options.TextFile))
        {
            errors.Add(L.CliErrorSourceRequired);
        }

        if (options.Compress && !string.IsNullOrWhiteSpace(options.TextFile))
        {
            errors.Add(L.CliErrorTextFileExtractOnly);
        }

        if (explicitPasswordSourceCount > 1)
        {
            errors.Add(L.CliErrorSinglePasswordSource);
        }

        if (options.CompressionLevel is < 0 or > 5)
        {
            errors.Add(L.CliErrorLevelRange);
        }

        // GLM-5.3, 2026-11-04：批次 5——并发度范围校验。
        if (options.MaxConcurrency is < 1 or > 16)
        {
            errors.Add(L.CliErrorConcurrencyRange);
        }

        // Antigravity, 2026-11-04：批次 10——日志保留天数范围校验（>= 1）。
        if (options.LogRetentionDays < 1)
        {
            errors.Add(L.CliErrorLogRetentionRange);
        }

        if (options.RecoveryRecord is < 0 or > 100)
        {
            errors.Add(L.CliErrorRecoveryRange);
        }

        if (options.MaxSizeGB < 0)
        {
            errors.Add(L.CliErrorMaxSizeNegative);
        }

        if (!ArchiveFormatCatalog.TryGet(options.Extension, out var formatDefinition))
        {
            errors.Add(string.Format(L.CliErrorUnknownFormat, options.Extension, ArchiveFormatCatalog.ExtractFormatsText));
        }
        else if (options.Compress && !formatDefinition.CanCreate)
        {
            errors.Add(string.Format(L.CliErrorFormatNotCreatable, options.Extension, ArchiveFormatCatalog.CreateFormatsText));
        }
        else if (options.Decompress && !formatDefinition.CanExtract)
        {
            errors.Add(string.Format(L.CliErrorFormatNotExtractable, options.Extension));
        }

        if (options.Compress && formatDefinition is not null &&
            explicitPasswordSourceCount > 0 && !formatDefinition.SupportsPassword)
        {
            errors.Add(string.Format(L.CliErrorFormatNoPassword, formatDefinition.Extension));
        }

        if (options.ExistingFileMode is not ("skip" or "update" or "overwrite"))
        {
            errors.Add(L.CliErrorExistingChoices);
        }
        if (options.PasswordName is not ("archive" or "base"))
        {
            errors.Add(L.CliErrorPasswordNameChoices);
        }
        if (options.ExistingFileMode == "update" && options.LockArchive)
        {
            errors.Add(L.CliErrorUpdateLockConflict);
        }

        if (options.VolumeUnit is not ("b" or "k" or "m" or "g" or "t"))
        {
            errors.Add(L.CliErrorVolumeUnitChoices);
        }

        if (!string.IsNullOrWhiteSpace(options.VolumeSize) &&
            (!double.TryParse(options.VolumeSize, NumberStyles.Float, CultureInfo.InvariantCulture, out var volumeSize) || volumeSize <= 0))
        {
            errors.Add(L.CliErrorVolumeSizePositive);
        }

        if (options.DeleteSource && options.MoveSource)
        {
            errors.Add(L.CliErrorDeleteMoveConflict);
        }

        if (options.Verbose && options.Quiet)
        {
            errors.Add(L.CliErrorVerboseQuietConflict);
        }

        ValidateExistingFile(options.TextFile, "--text-file", errors);
        ValidateExistingFile(options.PasswordFile, "--password-file", errors);
        ValidateExistingFile(options.CommentFile, "--comment", errors);
    }

    private static void ValidateExistingFile(string? path, string optionName, List<string> errors)
    {
        if (!string.IsNullOrWhiteSpace(path) && !File.Exists(path))
        {
            errors.Add(string.Format(L.CliErrorFileNotFound, optionName, path));
        }
    }

    private static string NormalizeFormat(string? format)
    {
        return (format ?? "rar").Trim().TrimStart('.').ToLowerInvariant();
    }

    private static string NormalizeVolumeUnit(string? unit)
    {
        return (unit ?? "g").Trim().ToLowerInvariant() switch
        {
            "bytes" => "b",
            "kb" => "k",
            "mb" => "m",
            "gb" => "g",
            "tb" => "t",
            var value => value
        };
    }
}
