using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using BatchCompress.Avalonia.Core.Models;
using BatchCompress.Avalonia.Core.Services;
using BatchCompress.Avalonia.Localization;

namespace BatchCompress.Avalonia;

// GPT-5, 2026-08-06：命令行数据对象覆盖 GUI 可脚本化的批处理能力。
// 密码可来自参数、文件或标准输入；进程原始输出明确不做脱敏，便于诊断压缩程序行为。
public sealed class CommandLineOptions
{
    public bool Compress { get; set; }
    public bool Decompress { get; set; }
    public bool Gui { get; set; } = true;
    public string? SourcePath { get; set; }
    public string[] InputPaths { get; set; } = [];
    public string? OutputPath { get; set; }
    public string? TextFile { get; set; }
    public string Extension { get; set; } = "rar";
    public bool UseRandomPassword { get; set; } = true;
    public string? Password { get; set; }
    public string? PasswordFile { get; set; }
    public bool ReadPasswordFromStandardInput { get; set; }
    public string PasswordName { get; set; } = "archive";
    // WorkBuddy, 2026-11-16：0.8.12——默认值统一读 ArchiveDefaults，与图形界面同源。
    public int CompressionLevel { get; set; } = ArchiveDefaults.DefaultCompressionLevel;
    public bool Solid { get; set; } = true;
    // 默认不分卷（DefaultEnableVolume 为 false），因此不设置分卷大小；用户显式传 --volume 才会启用。
    public string? VolumeSize { get; set; }
    public string VolumeUnit { get; set; } = ArchiveDefaults.DefaultVolumeUnit;
    public bool QuickOpen { get; set; }
    public bool TestArchive { get; set; }
    public string? CommentFile { get; set; }
    public string? TempDir { get; set; }
    public int RecoveryRecord { get; set; } = ArchiveDefaults.DefaultRecoveryRecordPercent;
    public bool LockArchive { get; set; }
    public string ExistingFileMode { get; set; } = "overwrite";
    public bool StrictExtract { get; set; }
    public bool SkipProcessed { get; set; } = true;
    public bool DeleteSource { get; set; }
    public bool MoveSource { get; set; }
    public double MaxSizeGB { get; set; } = 666;
    public bool ShutdownAfter { get; set; }
    public bool AddEnclosures { get; set; } = true;
    public string? EnclosureList { get; set; }
    public string[] EnclosurePaths { get; set; } = [];
    public string? LogFile { get; set; }
    // Antigravity, 2026-11-04：批次 10——日志过期保留天数（默认 7 天，支持自定义）。
    public int LogRetentionDays { get; set; } = FileLoggerService.DefaultRetentionDays;
    // Antigravity, 2026-11-08：批次 16（P3-13）——安全诊断包导出路径（.zip）。
    public string? ExportDiagnosticsPath { get; set; }
    // WorkBuddy, 2026-11-15：批次 20（P4-3）——逐项结果报告路径（.csv / .md）。
    // 与诊断包不同：报告只从结构化字段生成，默认不含密码与原始命令行。
    public string? ReportPath { get; set; }
    public bool Verbose { get; set; }
    public bool Quiet { get; set; }
    public bool DryRun { get; set; }

    // GLM-5.3, 2026-11-04：批次 5——受控并发度（默认 1 串行，1-16）。
    public int MaxConcurrency { get; set; } = 1;

    // WorkBuddy, 2026-11-15：批次 18（P4-2）——--concurrency auto 时按硬件资源自动定值，
    // 与显式数值互斥；auto 不改动 MaxConcurrency 的默认值，实际值在构建批次选项时解析。
    public bool AutoConcurrency { get; set; }

    // GLM-5.3, 2026-11-04：批次 3——CLI 界面语言与结构化输出格式。
    // Language 为 LocalizationService 的语言键（en-US 等区域变体在解析时归一为 en 等）；
    // OutputFormat 仅 text（人类可读）与 json（NDJSON 逐行事件流）。
    public string Language { get; set; } = "zh-CN";
    public string OutputFormat { get; set; } = "text";
}

// GPT-5, 2026-08-06：解析结果显式携带错误，入口进程可用退出码 2 拒绝无效参数，
// 不会在解析失败后继续使用默认值并意外启动 GUI。
public sealed class CommandLineParseOutcome
{
    public CommandLineOptions Options { get; init; } = new();
    public IReadOnlyList<string> Errors { get; init; } = [];
    public bool LanguageExplicitlySet { get; init; }
    public bool Success => Errors.Count == 0;
}

// GPT-5, 2026-08-06：使用项目内轻量解析器维护命令行契约，避免绑定 System.CommandLine 预览期 API。
// 旧的 --compress/--decompress 仍可使用，compress/extract 动词会在解析前规范为相同开关。
// 多语言帮助文本常量位于 CommandLineHandler.HelpTexts.cs。
public static partial class CommandLineHandler
{
    // GLM-5.3, 2026-11-04：批次 4——解析期错误文案接入 LocalizationService，
    // 跟随命令行中的 --language/--lang（无论其出现顺序，解析开始即按预扫描结果切换）。
    private static LanguageStrings L => LocalizationService.Instance.Strings;

    private enum ValueKind
    {
        Flag,
        Single,
        Multiple
    }

    private sealed record OptionDefinition(string CanonicalName, ValueKind Kind);

    private sealed class ParseState
    {
        public CommandLineOptions Options { get; } = new();
        public List<string> Errors { get; } = [];
        public bool ExplicitGui { get; set; }

        // GLM-5.3, 2026-11-04：批次 7——命令行出现过“有效的”--language/--lang 即置位，
        // 用于“纯 GUI 平滑启动”判定（见 ShouldDefaultToGui）。
        public bool LanguageRequested { get; set; }
        public bool RandomPassword { get; set; } = true;
        public bool NoRandomPassword { get; set; }
        public bool NoSolid { get; set; }
        public bool NoSkipProcessed { get; set; }
        public bool NoAddEnclosures { get; set; }
    }

    private static readonly IReadOnlyDictionary<string, OptionDefinition> Definitions =
        BuildDefinitions();

    public static CommandLineParseOutcome ParseArguments(string[] args)
    {
        ArgumentNullException.ThrowIfNull(args);
        var effectiveArgs = NormalizeVerb(args);

        // WorkBuddy, 2026-11-16：0.8.13——每次解析先清除显式语言标记；
        // 只有本次真正解析到有效 --lang 时才置位，避免静态状态跨调用残留。
        LocalizationService.Instance.LanguageExplicitlySet = false;

        // GLM-5.3, 2026-11-04：批次 4——解析期错误文案跟随 --language/--lang：
        // 即使语言参数出现在出错参数之后，报错也按该语言输出；
        // Program.Main 在解析成功后仍会统一同步一次，GUI 场景不受影响。
        var detectedLanguage = DetectLanguage(effectiveArgs);
        if (detectedLanguage != null)
        {
            LocalizationService.Instance.CurrentLanguage = detectedLanguage;
        }

        var state = new ParseState();

        for (var index = 0; index < effectiveArgs.Length; index++)
        {
            var token = effectiveArgs[index];
            if (token == "--")
            {
                AddRemainingAsInputs(effectiveArgs, index + 1, state.Options.InputPaths, out var allInputs);
                state.Options.InputPaths = allInputs;
                break;
            }

            if (!TryReadOptionToken(token, out var name, out var inlineValue) ||
                !Definitions.TryGetValue(name, out var definition))
            {
                state.Errors.Add(string.Format(L.CliErrorUnknownOption, token));
                continue;
            }

            if (definition.Kind == ValueKind.Flag)
            {
                ApplyFlag(definition.CanonicalName, ParseFlagValue(token, inlineValue, state.Errors), state);
                continue;
            }

            if (definition.Kind == ValueKind.Single)
            {
                var value = ReadSingleValue(effectiveArgs, ref index, token, inlineValue, state.Errors);
                if (value is not null)
                {
                    ApplySingleValue(definition.CanonicalName, value, state);
                }
                continue;
            }

            var values = ReadMultipleValues(effectiveArgs, ref index, token, inlineValue, state.Errors);
            if (values.Count > 0)
            {
                ApplyMultipleValues(definition.CanonicalName, values, state);
            }
        }

        var options = state.Options;
        options.Extension = NormalizeFormat(options.Extension);
        options.VolumeUnit = NormalizeVolumeUnit(options.VolumeUnit);
        options.ExistingFileMode = (options.ExistingFileMode ?? "overwrite").Trim().ToLowerInvariant();
        options.PasswordName = (options.PasswordName ?? "archive").Trim().ToLowerInvariant();
        // GLM-5.3, 2026-11-04：批次 7——放宽默认模式：
        // 无参数（原有行为）或仅携带有效 --language/--lang 且无任何批处理来源/输出意图时，
        // 默认进入 GUI 模式（如 `--lang en` 直接以英文启动桌面程序）。
        options.Gui = effectiveArgs.Length == 0 || state.ExplicitGui ||
                      ShouldDefaultToGui(options, state.LanguageRequested);
        options.Solid = options.Solid && !state.NoSolid;
        options.SkipProcessed = options.SkipProcessed && !state.NoSkipProcessed;
        options.AddEnclosures = options.AddEnclosures && !state.NoAddEnclosures;

        var explicitPasswordSourceCount = new[]
        {
            !string.IsNullOrEmpty(options.Password),
            !string.IsNullOrWhiteSpace(options.PasswordFile),
            options.ReadPasswordFromStandardInput
        }.Count(value => value);
        options.UseRandomPassword = state.RandomPassword && !state.NoRandomPassword && explicitPasswordSourceCount == 0;
        if (ArchiveFormatCatalog.TryGet(options.Extension, out var selectedFormat) &&
            !selectedFormat.SupportsPassword)
        {
            // 非加密格式不能因为全局默认随机密码而被构造器误传 -p 参数。
            options.UseRandomPassword = false;
        }

        Validate(options, state.ExplicitGui, state.LanguageRequested, explicitPasswordSourceCount, effectiveArgs, state.Errors);
        return new CommandLineParseOutcome
        {
            Options = options,
            Errors = state.Errors,
            LanguageExplicitlySet = state.LanguageRequested
        };
    }

    public static bool IsHelpRequested(string[] args)
    {
        ArgumentNullException.ThrowIfNull(args);
        return args.Any(argument => argument is "--help" or "-h" or "-?" or "/?");
    }

    public static bool IsVersionRequested(string[] args)
    {
        ArgumentNullException.ThrowIfNull(args);
        return args.Any(argument => argument.Equals("--version", StringComparison.OrdinalIgnoreCase));
    }

    private static IReadOnlyDictionary<string, OptionDefinition> BuildDefinitions()
    {
        var definitions = new Dictionary<string, OptionDefinition>(StringComparer.OrdinalIgnoreCase);

        Add(definitions, "--compress", ValueKind.Flag, "--compress", "-c");
        Add(definitions, "--decompress", ValueKind.Flag, "--decompress", "-d");
        Add(definitions, "--gui", ValueKind.Flag, "--gui", "-g");
        Add(definitions, "--source", ValueKind.Single, "--source", "-s");
        Add(definitions, "--input", ValueKind.Multiple, "--input", "-i");
        Add(definitions, "--output", ValueKind.Single, "--output", "-o");
        Add(definitions, "--text-file", ValueKind.Single, "--text-file", "-t");
        Add(definitions, "--extension", ValueKind.Single, "--extension", "--format", "-e");
        Add(definitions, "--random-password", ValueKind.Flag, "--random-password", "-r");
        Add(definitions, "--no-random-password", ValueKind.Flag, "--no-random-password");
        Add(definitions, "--password", ValueKind.Single, "--password", "-p");
        Add(definitions, "--password-file", ValueKind.Single, "--password-file");
        Add(definitions, "--password-stdin", ValueKind.Flag, "--password-stdin");
        Add(definitions, "--password-name", ValueKind.Single, "--password-name");
        Add(definitions, "--level", ValueKind.Single, "--level", "-l");
        Add(definitions, "--solid", ValueKind.Flag, "--solid");
        Add(definitions, "--no-solid", ValueKind.Flag, "--no-solid");
        Add(definitions, "--volume-size", ValueKind.Single, "--volume-size", "-v");
        Add(definitions, "--volume-unit", ValueKind.Single, "--volume-unit");
        Add(definitions, "--quick-open", ValueKind.Flag, "--quick-open");
        Add(definitions, "--test", ValueKind.Flag, "--test");
        Add(definitions, "--comment", ValueKind.Single, "--comment");
        Add(definitions, "--temp-dir", ValueKind.Single, "--temp-dir");
        Add(definitions, "--recovery", ValueKind.Single, "--recovery");
        Add(definitions, "--existing", ValueKind.Single, "--existing");
        Add(definitions, "--strict-extract", ValueKind.Flag, "--strict-extract");
        Add(definitions, "--lock", ValueKind.Flag, "--lock");
        Add(definitions, "--skip-processed", ValueKind.Flag, "--skip-processed");
        Add(definitions, "--no-skip-processed", ValueKind.Flag, "--no-skip-processed");
        Add(definitions, "--delete-source", ValueKind.Flag, "--delete-source");
        Add(definitions, "--move-source", ValueKind.Flag, "--move-source");
        Add(definitions, "--max-size", ValueKind.Single, "--max-size", "--max-size-gb");
        Add(definitions, "--shutdown", ValueKind.Flag, "--shutdown");
        Add(definitions, "--add-enclosures", ValueKind.Flag, "--add-enclosures");
        Add(definitions, "--no-add-enclosures", ValueKind.Flag, "--no-add-enclosures");
        Add(definitions, "--enclosure-list", ValueKind.Single, "--enclosure-list");
        Add(definitions, "--enclosure", ValueKind.Multiple, "--enclosure");
        Add(definitions, "--log-file", ValueKind.Single, "--log-file");
        Add(definitions, "--log-retention-days", ValueKind.Single, "--log-retention-days", "--log-retention");
        Add(definitions, "--verbose", ValueKind.Flag, "--verbose");
        Add(definitions, "--quiet", ValueKind.Flag, "--quiet", "-q");
        Add(definitions, "--dry-run", ValueKind.Flag, "--dry-run");
        // GLM-5.3, 2026-11-04：批次 3——语言与结构化输出。注意 --format 已是 --extension
        // 的历史别名，不能挪用为输出格式别名，输出格式仅使用 --output-format。
        Add(definitions, "--language", ValueKind.Single, "--language", "--lang");
        Add(definitions, "--output-format", ValueKind.Single, "--output-format");
        Add(definitions, "--concurrency", ValueKind.Single, "--concurrency", "-j");
        // Antigravity, 2026-11-08：批次 16（P3-13）——脱敏诊断报告导出选项。
        Add(definitions, "--export-diagnostics", ValueKind.Single, "--export-diagnostics");
        // WorkBuddy, 2026-11-15：批次 20（P4-3）——批处理结果报告导出。
        Add(definitions, "--report", ValueKind.Single, "--report");

        return definitions;
    }

    private static void Add(
        Dictionary<string, OptionDefinition> definitions,
        string canonicalName,
        ValueKind kind,
        params string[] aliases)
    {
        var definition = new OptionDefinition(canonicalName, kind);
        foreach (var alias in aliases)
        {
            definitions[alias] = definition;
        }
    }

    private static string[] NormalizeVerb(string[] args)
    {
        if (args.Length == 0)
        {
            return args;
        }

        var mode = args[0].Trim().ToLowerInvariant();
        var replacement = mode switch
        {
            "compress" => "--compress",
            "extract" or "decompress" => "--decompress",
            "gui" => "--gui",
            _ => null
        };
        return replacement == null ? args : [replacement, .. args.Skip(1)];
    }

    private static bool TryReadOptionToken(string token, out string name, out string? inlineValue)
    {
        name = string.Empty;
        inlineValue = null;
        if (string.IsNullOrWhiteSpace(token) ||
            token == "-" ||
            !token.StartsWith("-", StringComparison.Ordinal) ||
            LooksLikeNegativeNumber(token))
        {
            return false;
        }

        var equalsIndex = token.IndexOf('=');
        if (equalsIndex < 0)
        {
            name = token;
            return true;
        }

        name = token[..equalsIndex];
        inlineValue = token[(equalsIndex + 1)..];
        return !string.IsNullOrWhiteSpace(name);
    }

    private static bool LooksLikeNegativeNumber(string token)
    {
        return token.Length > 1 && char.IsDigit(token[1]);
    }

    private static bool ParseFlagValue(string token, string? inlineValue, List<string> errors)
    {
        if (inlineValue is null)
        {
            return true;
        }

        if (bool.TryParse(inlineValue, out var value))
        {
            return value;
        }

        if (inlineValue is "1" or "yes" or "on")
        {
            return true;
        }

        if (inlineValue is "0" or "no" or "off")
        {
            return false;
        }

        errors.Add(string.Format(L.CliErrorFlagBoolean, token));
        return true;
    }

    private static string? ReadSingleValue(
        string[] args,
        ref int index,
        string token,
        string? inlineValue,
        List<string> errors)
    {
        if (inlineValue is not null)
        {
            return inlineValue;
        }

        if (index + 1 >= args.Length || args[index + 1] == "--")
        {
            errors.Add(string.Format(L.CliErrorMissingValue, token));
            return null;
        }

        index++;
        return args[index];
    }

    private static List<string> ReadMultipleValues(
        string[] args,
        ref int index,
        string token,
        string? inlineValue,
        List<string> errors)
    {
        var values = new List<string>();
        if (inlineValue is not null)
        {
            if (!string.IsNullOrWhiteSpace(inlineValue))
            {
                values.Add(inlineValue);
            }
        }
        else
        {
            while (index + 1 < args.Length && !IsOptionBoundary(args[index + 1]))
            {
                index++;
                values.Add(args[index]);
            }
        }

        if (values.Count == 0)
        {
            errors.Add(string.Format(L.CliErrorMissingValue, token));
        }

        return values;
    }

    private static bool IsOptionBoundary(string token)
    {
        return token == "--" ||
               (TryReadOptionToken(token, out _, out _) && !LooksLikeNegativeNumber(token));
    }

    private static void ApplyFlag(string canonicalName, bool value, ParseState state)
    {
        switch (canonicalName)
        {
            case "--compress":
                state.Options.Compress = value;
                break;
            case "--decompress":
                state.Options.Decompress = value;
                break;
            case "--gui":
                state.ExplicitGui = value;
                break;
            case "--random-password":
                state.RandomPassword = value;
                break;
            case "--no-random-password":
                state.NoRandomPassword = value;
                break;
            case "--password-stdin":
                state.Options.ReadPasswordFromStandardInput = value;
                break;
            case "--solid":
                state.Options.Solid = value;
                break;
            case "--no-solid":
                state.NoSolid = value;
                break;
            case "--quick-open":
                state.Options.QuickOpen = value;
                break;
            case "--test":
                state.Options.TestArchive = value;
                break;
            case "--lock":
                state.Options.LockArchive = value;
                break;
            case "--strict-extract":
                state.Options.StrictExtract = value;
                break;
            case "--skip-processed":
                state.Options.SkipProcessed = value;
                break;
            case "--no-skip-processed":
                state.NoSkipProcessed = value;
                break;
            case "--delete-source":
                state.Options.DeleteSource = value;
                break;
            case "--move-source":
                state.Options.MoveSource = value;
                break;
            case "--shutdown":
                state.Options.ShutdownAfter = value;
                break;
            case "--add-enclosures":
                state.Options.AddEnclosures = value;
                break;
            case "--no-add-enclosures":
                state.NoAddEnclosures = value;
                break;
            case "--verbose":
                state.Options.Verbose = value;
                break;
            case "--quiet":
                state.Options.Quiet = value;
                break;
            case "--dry-run":
                state.Options.DryRun = value;
                break;
        }
    }

    private static void ApplySingleValue(string canonicalName, string value, ParseState state)
    {
        switch (canonicalName)
        {
            case "--source":
                state.Options.SourcePath = value;
                break;
            case "--output":
                state.Options.OutputPath = value;
                break;
            case "--text-file":
                state.Options.TextFile = value;
                break;
            case "--extension":
                state.Options.Extension = value;
                break;
            case "--password":
                state.Options.Password = value;
                break;
            case "--password-file":
                state.Options.PasswordFile = value;
                break;
            case "--password-name":
                state.Options.PasswordName = value;
                break;
            case "--level":
                state.Options.CompressionLevel = ParseInt(value, "--level", state.Errors, 3);
                break;
            case "--volume-size":
                state.Options.VolumeSize = value;
                break;
            case "--volume-unit":
                state.Options.VolumeUnit = value;
                break;
            case "--comment":
                state.Options.CommentFile = value;
                break;
            case "--temp-dir":
                state.Options.TempDir = value;
                break;
            case "--recovery":
                state.Options.RecoveryRecord = ParseInt(value, "--recovery", state.Errors, 3);
                break;
            case "--existing":
                state.Options.ExistingFileMode = value;
                break;
            case "--lock":
                state.Options.LockArchive = true;
                break;
            case "--max-size":
                state.Options.MaxSizeGB = ParseDouble(value, "--max-size", state.Errors, 666);
                break;
            case "--enclosure-list":
                state.Options.EnclosureList = value;
                break;
            case "--log-file":
                state.Options.LogFile = value;
                break;
            case "--language":
                state.Options.Language = NormalizeLanguage(value, state.Errors);
                // GLM-5.3, 2026-11-04：批次 7——只有“有效的”语言值才允许触发默认 GUI
                //（无效值已由上方 NormalizeLanguage 记录错误，退出码 2 路径不受影响）。
                if (TryNormalizeLanguage(value, out _))
                {
                    state.LanguageRequested = true;
                    // WorkBuddy, 2026-11-16：0.8.13——图形界面需要知道语言是用户显式指定的，
                    // 不能再用系统语言把它覆盖掉。
                    LocalizationService.Instance.LanguageExplicitlySet = true;
                }
                break;
            case "--output-format":
                state.Options.OutputFormat = NormalizeOutputFormat(value, state.Errors);
                break;
            case "--concurrency":
                // WorkBuddy, 2026-11-15：批次 18（P4-2）——接受 auto 关键字，
                // 大小写不敏感；其余值仍按原有整数解析与 1-16 范围校验处理。
                if (string.Equals(value, "auto", StringComparison.OrdinalIgnoreCase))
                {
                    state.Options.AutoConcurrency = true;
                }
                else
                {
                    state.Options.MaxConcurrency = ParseInt(value, "--concurrency", state.Errors, 1);
                }
                break;
            case "--log-retention-days":
                state.Options.LogRetentionDays = ParseInt(value, "--log-retention-days", state.Errors, FileLoggerService.DefaultRetentionDays);
                break;
            case "--export-diagnostics":
                state.Options.ExportDiagnosticsPath = value;
                break;
            case "--report":
                state.Options.ReportPath = value;
                break;
        }
    }

    private static void ApplyMultipleValues(string canonicalName, IEnumerable<string> values, ParseState state)
    {
        switch (canonicalName)
        {
            case "--input":
                state.Options.InputPaths = [.. state.Options.InputPaths, .. values];
                break;
            case "--enclosure":
                state.Options.EnclosurePaths = [.. state.Options.EnclosurePaths, .. values];
                break;
        }
    }

    private static int ParseInt(string value, string optionName, List<string> errors, int fallback)
    {
        if (int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsed))
        {
            return parsed;
        }

        errors.Add(string.Format(L.CliErrorMustBeInteger, optionName));
        return fallback;
    }

    private static double ParseDouble(string value, string optionName, List<string> errors, double fallback)
    {
        if (double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out var parsed))
        {
            return parsed;
        }

        errors.Add(string.Format(L.CliErrorMustBeNumber, optionName));
        return fallback;
    }

    private static void AddRemainingAsInputs(string[] args, int startIndex, string[] existing, out string[] inputs)
    {
        inputs = [.. existing, .. args.Skip(startIndex)];
    }
}
