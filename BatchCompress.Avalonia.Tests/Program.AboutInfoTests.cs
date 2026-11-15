using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Threading.Tasks;
using BatchCompress.Avalonia.Core.Services;
using BatchCompress.Avalonia.Localization;

// WorkBuddy, 2026-11-16：批次 27（P4-4）——「关于」弹窗展示值、路径口径、
// 五语言词条以及「导出诊断」确实能收到界面日志与崩溃日志的回归守护。
internal static partial class Program
{
    private static readonly string[] AboutLocalizationKeys =
    [
        "About", "TooltipAbout", "AboutVersion", "AboutRuntime", "AboutOperatingSystem",
        "AboutLogDirectory", "AboutCrashLogDirectory", "AboutReportDirectory",
        "AboutOpenLogDirectory", "AboutClose", "AboutOpenFolderFailed", "AboutDirectoryNote"
    ];

    private static Task TestAboutInfoAndPaths()
    {
        // 崩溃日志目录可能被前面的测试临时覆盖，这里显式恢复生产口径。
        CrashLogService.DirectoryOverrideForTests = null;

        var version = ApplicationInfoService.ReadApplicationVersion();
        Assert(!string.IsNullOrWhiteSpace(version) && version != "unknown", "版本号不能为空");

        // 与 Program.cs 的 --version 走同一个程序集特性，避免弹窗显示的版本和命令行不一致。
        var expectedVersion = typeof(ApplicationInfoService).Assembly
            .GetCustomAttribute<AssemblyInformationalVersionAttribute>()!
            .InformationalVersion.Split('+')[0];
        AssertEqual(expectedVersion, version);

        var info = ApplicationInfoService.Create();
        Assert(!string.IsNullOrWhiteSpace(info.Runtime), "运行时描述不能为空");
        Assert(info.Runtime.Contains(".NET", StringComparison.OrdinalIgnoreCase), "运行时描述应包含 .NET");
        Assert(info.OperatingSystem.Contains(RuntimeInformation.OSArchitecture.ToString(), StringComparison.Ordinal),
            "操作系统描述应包含架构");

        // 界面日志目录必须是绝对路径、以 logs 结尾，且与文件日志器使用的目录同源。
        Assert(Path.IsPathRooted(info.GuiLogDirectory), "界面日志目录应为绝对路径");
        AssertEqual("logs", Path.GetFileName(info.GuiLogDirectory));
        AssertEqual(ApplicationInfoService.ResolveGuiLogDirectory(), info.GuiLogDirectory);
        AssertEqual(CrashLogService.LogDirectory, info.CrashLogDirectory);

        // 报告默认目录必须真实存在（桌面或系统临时目录），否则「打开/导出」会静默失败。
        Assert(Directory.Exists(info.ReportDirectory), "报告默认目录必须存在");
        AssertEqual(ApplicationInfoService.ResolveReportDirectory(), info.ReportDirectory);

        // 诊断扫描范围必须同时覆盖界面日志与崩溃日志，且已去重。
        var directories = info.DiagnosticsLogDirectories;
        Assert(directories.Count >= 2, "诊断扫描目录至少应包含界面日志与崩溃日志两处");
        Assert(directories.Contains(info.GuiLogDirectory, StringComparer.Ordinal), "诊断扫描缺少界面日志目录");
        Assert(directories.Contains(info.CrashLogDirectory, StringComparer.Ordinal), "诊断扫描缺少崩溃日志目录");
        AssertEqual(directories.Count,
            directories.Distinct(OperatingSystem.IsLinux() ? StringComparer.Ordinal : StringComparer.OrdinalIgnoreCase).Count());

        return Task.CompletedTask;
    }

    private static async Task TestDiagnosticsCollectsCrashAndGuiLogs()
    {
        var root = Path.Combine(Path.GetTempPath(), $"about-diag-{Guid.NewGuid():N}");
        var guiLogs = Path.Combine(root, "gui", "logs");
        var crashLogs = Path.Combine(root, "crash", "logs");
        var zipPath = Path.Combine(root, "diagnostics.zip");
        Directory.CreateDirectory(guiLogs);
        Directory.CreateDirectory(crashLogs);

        const string guiSecret = "GuiPlainTextPassword";
        const string crashSecret = "CrashPlainTextPassword";

        try
        {
            File.WriteAllText(
                Path.Combine(guiLogs, "batchcompress_gui_20261116_030000.log"),
                $"[INFO] rar a -p{guiSecret} out.rar in.txt\n");
            File.WriteAllText(
                Path.Combine(crashLogs, "batchcompress-crash_20261116_030000_000_123_1.log"),
                $"[FATAL] --password \"{crashSecret}\" unhandled\n");
            // 两个目录放同名文件，验证导出不会因 ZipArchive 重复项而失败。
            File.WriteAllText(
                Path.Combine(crashLogs, "batchcompress_gui_20261116_030000.log"),
                "[INFO] duplicate filename from another directory\n");

            var exporter = new DiagnosticsExportService(new[] { guiLogs, crashLogs, guiLogs });
            await exporter.ExportAsync(zipPath);

            using var archive = ZipFile.OpenRead(zipPath);
            var logEntries = archive.Entries
                .Where(entry => entry.FullName.StartsWith("logs/", StringComparison.Ordinal))
                .ToList();
            AssertEqual(3, logEntries.Count);
            Assert(logEntries.Any(entry => entry.FullName.Contains("batchcompress-crash_", StringComparison.Ordinal)),
                "崩溃日志必须进入诊断包");
            Assert(logEntries.Select(entry => entry.FullName).Distinct().Count() == 3,
                "跨目录同名文件必须生成不同的条目名");

            var allText = string.Empty;
            foreach (var entry in logEntries)
            {
                using var reader = new StreamReader(entry.Open());
                allText += await reader.ReadToEndAsync();
            }

            Assert(!allText.Contains(guiSecret, StringComparison.Ordinal), "界面日志中的明文密码必须被脱敏");
            Assert(!allText.Contains(crashSecret, StringComparison.Ordinal), "崩溃日志中的明文密码必须被脱敏");

            var summary = archive.GetEntry("diagnostics_summary.json");
            Assert(summary != null, "诊断包必须包含摘要");
            using var summaryReader = new StreamReader(summary!.Open());
            var summaryText = await summaryReader.ReadToEndAsync();
            Assert(summaryText.Contains("LogDirectories", StringComparison.Ordinal), "摘要应列出扫描过的日志目录");
        }
        finally
        {
            if (Directory.Exists(root))
            {
                Directory.Delete(root, recursive: true);
            }
        }
    }

    private static Task TestAboutLocalizationKeys()
    {
        var declared = typeof(LanguageStrings).GetProperties()
            .Where(property => property.PropertyType == typeof(string))
            .Select(property => property.Name)
            .ToHashSet(StringComparer.Ordinal);

        foreach (var language in LocalizationLanguages)
        {
            var strings = LocalizationLoader.Load(language);
            foreach (var key in AboutLocalizationKeys)
            {
                Assert(declared.Contains(key), $"LanguageStrings 缺少 {key}");
                var value = typeof(LanguageStrings).GetProperty(key)!.GetValue(strings) as string;
                Assert(!string.IsNullOrWhiteSpace(value), $"{language} 的 {key} 为空");
            }

            // string.Format 占位符必须保留，否则打开目录失败时会抛格式异常。
            Assert(strings.AboutOpenFolderFailed.Contains("{0}", StringComparison.Ordinal),
                $"{language} 的 AboutOpenFolderFailed 缺少 {{0}} 占位符");
        }

        return Task.CompletedTask;
    }

    private static Task TestAboutWindowWiring()
    {
        var root = FindRepositoryRoot();
        var aboutAxaml = Path.Combine(root, "Views", "AboutWindow.axaml");
        var mainAxaml = Path.Combine(root, "Views", "MainWindow.axaml");
        var mainCodeBehind = Path.Combine(root, "Views", "MainWindow.axaml.cs");

        Assert(File.Exists(aboutAxaml), "缺少 Views/AboutWindow.axaml");
        var about = File.ReadAllText(aboutAxaml);
        Assert(about.Contains("Binding AboutInfo.Version", StringComparison.Ordinal), "关于窗口未绑定版本");
        Assert(about.Contains("Binding AboutInfo.GuiLogDirectory", StringComparison.Ordinal), "关于窗口未绑定界面日志目录");
        Assert(about.Contains("Binding AboutInfo.CrashLogDirectory", StringComparison.Ordinal), "关于窗口未绑定崩溃日志目录");
        Assert(about.Contains("OpenAboutLogDirectoryCommand", StringComparison.Ordinal), "关于窗口缺少打开日志目录命令");
        Assert(about.Contains("ExportDiagnosticsCommand", StringComparison.Ordinal), "关于窗口缺少导出诊断命令");

        Assert(File.ReadAllText(mainAxaml).Contains("ShowAboutCommand", StringComparison.Ordinal),
            "主界面缺少「关于」入口按钮");
        Assert(File.ReadAllText(mainCodeBehind).Contains("ShowAboutRequested = ShowAboutAsync", StringComparison.Ordinal),
            "主界面未把「关于」请求接到窗口创建逻辑");

        return Task.CompletedTask;
    }
}
