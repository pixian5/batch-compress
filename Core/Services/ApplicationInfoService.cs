using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.InteropServices;

namespace BatchCompress.Avalonia.Core.Services;

/// <summary>
/// 「关于」弹窗展示的运行环境与路径快照。全部是只读值，不引用任何界面类型。
/// </summary>
/// <param name="Version">与命令行 --version 同源的产品版本。</param>
/// <param name="Runtime">.NET 运行时描述。</param>
/// <param name="OperatingSystem">操作系统与架构描述。</param>
/// <param name="GuiLogDirectory">界面运行日志的实际写入目录。</param>
/// <param name="CrashLogDirectory">未处理异常崩溃日志的实际写入目录。</param>
/// <param name="ReportDirectory">界面导出结果报告的默认目录。</param>
/// <param name="DiagnosticsLogDirectories">诊断包扫描的全部日志目录。</param>
public sealed record ApplicationInfo(
    string Version,
    string Runtime,
    string OperatingSystem,
    string GuiLogDirectory,
    string CrashLogDirectory,
    string ReportDirectory,
    IReadOnlyList<string> DiagnosticsLogDirectories);

/// <summary>
/// 统一计算版本、运行时与各类产物路径。
/// WorkBuddy, 2026-11-16：批次 27（P4-4）——此前界面日志、崩溃日志、诊断导出、
/// 结果报告各自拼路径：界面日志写用户数据目录，诊断导出却只扫应用包内的 logs，
/// 而且只认 batchcompress_*.log*，崩溃日志 batchcompress-crash_*.log 从来收不进来。
/// 这里把口径收成一处，写入方、展示方、收集方全部复用，避免「关于」里显示的和实际不一致。
/// </summary>
public static class ApplicationInfoService
{
    public const string GuiLogFolderName = "BatchCompress.Avalonia";

    /// <summary>读取程序集信息版本，与 Program.cs 的 --version 走同一个特性。</summary>
    public static string ReadApplicationVersion()
    {
        var informational = typeof(ApplicationInfoService).Assembly
            .GetCustomAttribute<AssemblyInformationalVersionAttribute>()?
            .InformationalVersion;
        return string.IsNullOrWhiteSpace(informational) ? "unknown" : informational.Split('+')[0];
    }

    /// <summary>界面运行日志目录；与 MainWindowViewModel 创建文件日志器时的目录完全一致。</summary>
    public static string ResolveGuiLogDirectory()
    {
        var applicationData = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
        if (string.IsNullOrWhiteSpace(applicationData))
        {
            applicationData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        }

        var baseDirectory = string.IsNullOrWhiteSpace(applicationData)
            ? AppContext.BaseDirectory
            : applicationData;
        return Path.Combine(baseDirectory, GuiLogFolderName, "logs");
    }

    /// <summary>命令行默认日志目录，也是未显式指定 --log-file 时的落点。</summary>
    public static string ResolveCliLogDirectory() => Path.Combine(AppContext.BaseDirectory, "logs");

    /// <summary>界面导出结果报告的默认目录；桌面不可用时退回系统临时目录。</summary>
    public static string ResolveReportDirectory()
    {
        var desktop = Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory);
        return string.IsNullOrWhiteSpace(desktop) || !Directory.Exists(desktop)
            ? Path.GetTempPath()
            : desktop;
    }

    /// <summary>
    /// 诊断包需要同时覆盖界面日志、崩溃日志与命令行日志。
    /// 默认后两者同目录，去重后通常只剩两个目录。
    /// </summary>
    public static IReadOnlyList<string> ResolveDiagnosticsLogDirectories()
    {
        var comparer = OperatingSystem.IsLinux()
            ? StringComparer.Ordinal
            : StringComparer.OrdinalIgnoreCase;

        return new[]
            {
                ResolveGuiLogDirectory(),
                CrashLogService.LogDirectory,
                ResolveCliLogDirectory()
            }
            .Where(path => !string.IsNullOrWhiteSpace(path))
            .Distinct(comparer)
            .ToArray();
    }

    public static ApplicationInfo Create() => new(
        ReadApplicationVersion(),
        RuntimeInformation.FrameworkDescription,
        $"{RuntimeInformation.OSDescription} ({RuntimeInformation.OSArchitecture})",
        ResolveGuiLogDirectory(),
        CrashLogService.LogDirectory,
        ResolveReportDirectory(),
        ResolveDiagnosticsLogDirectories());
}
