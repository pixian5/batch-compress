using System;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using BatchCompress.Avalonia.Core.Services;

// Gemini 4 Hydrogen, 2026-11-27：批次 46——核心分层隔离、测试编译通配治理、诊断路由注入与源码规模守护。
internal static partial class Program
{
    private static async Task TestCoreIsolationAndProjectHygiene()
    {
        var root = FindRepositoryRoot();

        // 1. 测试工程必须采用目录通配引入 Core/Localization/CommandLineHandler/测试分部类，仅显式排除 SystemIntegrationService。
        var testCsprojPath = Path.Combine(root, "BatchCompress.Avalonia.Tests", "BatchCompress.Avalonia.Tests.csproj");
        var testCsproj = File.ReadAllText(testCsprojPath);
        Assert(testCsproj.Contains("<Compile Include=\"Program*.cs\" />", StringComparison.Ordinal),
            "Tests.csproj 应使用 Program*.cs 通配符自动纳入新增测试文件");
        Assert(testCsproj.Contains("<Compile Include=\"..\\Core\\**\\*.cs\"", StringComparison.Ordinal),
            "Tests.csproj 应使用 ..\\Core\\**\\*.cs 通配符自动纳入所有核心源码");
        Assert(testCsproj.Contains("SystemIntegrationService.cs", StringComparison.Ordinal) &&
               testCsproj.Contains("<Compile Remove=", StringComparison.Ordinal),
            "Tests.csproj 仅应排除直接依赖 Avalonia 窗口系统的 SystemIntegrationService.cs");
        Assert(testCsproj.Contains("<Compile Include=\"..\\Localization\\*.cs\"", StringComparison.Ordinal),
            "Tests.csproj 应使用 ..\\Localization\\*.cs 通配符纳入全部本地化源码");

        // 2. Core（除 SystemIntegrationService）、Localization、CLI 入口与纯逻辑 ViewModels 严禁反向引入 UI 框架命名空间。
        var forbiddenUiImports = new[]
        {
            "using Avalonia",
            "using CommunityToolkit",
            "using Tmds.DBus"
        };

        var pureFiles = Directory.EnumerateFiles(Path.Combine(root, "Core"), "*.cs", SearchOption.AllDirectories)
            .Where(path => !path.EndsWith("SystemIntegrationService.cs", StringComparison.OrdinalIgnoreCase))
            .Concat(Directory.EnumerateFiles(Path.Combine(root, "Localization"), "*.cs", SearchOption.TopDirectoryOnly))
            .Concat(Directory.EnumerateFiles(root, "CommandLineHandler*.cs", SearchOption.TopDirectoryOnly))
            .Concat(
            [
                Path.Combine(root, "HeadlessBatchRunner.cs"),
                Path.Combine(root, "WindowsConsoleBootstrap.cs"),
                Path.Combine(root, "ViewModels", "TabVisibility.cs"),
                Path.Combine(root, "ViewModels", "OperationCompletionSignal.cs"),
                Path.Combine(root, "ViewModels", "DeleteSourceConfirmationGuard.cs"),
                Path.Combine(root, "ViewModels", "LogSearchFilter.cs"),
                Path.Combine(root, "ViewModels", "FailedItemRetryList.cs"),
                Path.Combine(root, "ViewModels", "LogBuffer.cs")
            ]);

        foreach (var file in pureFiles)
        {
            var text = File.ReadAllText(file);
            foreach (var forbidden in forbiddenUiImports)
            {
                Assert(!Regex.IsMatch(text, @$"^\s*{Regex.Escape(forbidden)}\b", RegexOptions.Multiline),
                    $"{Path.GetRelativePath(root, file)} 严禁引入 UI 依赖 ({forbidden})，以免破坏 Core 与轻依赖测试隔离");
            }
        }

        // 3. 验证全仓所有生产源码与测试源码均控制在 1000 行以内，防止大文件臃肿回潮。
        var checkedSourceFiles = Directory.EnumerateFiles(root, "*.cs", SearchOption.AllDirectories)
            .Where(path =>
            {
                var rel = Path.GetRelativePath(root, path).Replace('\\', '/');
                return !rel.StartsWith("old/", StringComparison.OrdinalIgnoreCase) &&
                       !rel.StartsWith("bin/", StringComparison.OrdinalIgnoreCase) &&
                       !rel.StartsWith("obj/", StringComparison.OrdinalIgnoreCase) &&
                       !rel.StartsWith("BatchCompress.Avalonia/", StringComparison.OrdinalIgnoreCase) &&
                       !rel.StartsWith("BatchCompress.Avalonia.Tests/bin/", StringComparison.OrdinalIgnoreCase) &&
                       !rel.StartsWith("BatchCompress.Avalonia.Tests/obj/", StringComparison.OrdinalIgnoreCase) &&
                       !rel.Contains("/bin/", StringComparison.OrdinalIgnoreCase) &&
                       !rel.Contains("/obj/", StringComparison.OrdinalIgnoreCase);
            });

        foreach (var file in checkedSourceFiles)
        {
            var lineCount = File.ReadAllLines(file).Length;
            Assert(lineCount < 1000,
                $"{Path.GetRelativePath(root, file)} 共 {lineCount} 行，超过 1000 行上限，请按职责拆分为 partial class");
        }

        // 4. 诊断导出必须经由注入引擎工厂构造，且 Program.cs 同步显式语言标记；验证真实注入引擎导出行为。
        var vmExecution = File.ReadAllText(Path.Combine(root, "ViewModels", "MainWindowViewModel.Execution.cs"));
        var cliRunner = File.ReadAllText(Path.Combine(root, "HeadlessBatchRunner.cs"));
        Assert(vmExecution.Contains("DiagnosticsExportService.Create(", StringComparison.Ordinal) &&
               !vmExecution.Contains("new RarArchiveEngine()", StringComparison.Ordinal),
            "MainWindowViewModel.Execution.cs 应复用 DiagnosticsExportService.Create(..., _archiveEngine)");
        Assert(cliRunner.Contains("DiagnosticsExportService.Create(", StringComparison.Ordinal) &&
               !cliRunner.Contains("new RarArchiveEngine()", StringComparison.Ordinal),
            "HeadlessBatchRunner.cs 应复用 DiagnosticsExportService.Create(..., _archiveEngine)");

        var programCs = File.ReadAllText(Path.Combine(root, "Program.cs"));
        Assert(programCs.Contains("parseOutcome.LanguageExplicitlySet", StringComparison.Ordinal),
            "Program.cs 应显式同步 parseOutcome.LanguageExplicitlySet");

        var tempDir = Path.Combine(Path.GetTempPath(), $"bc-diag-create-{Guid.NewGuid():N}");
        Directory.CreateDirectory(tempDir);
        try
        {
            var routedExporter = DiagnosticsExportService.Create(
                [tempDir],
                new ArchiveEngineRouter(
                    new FallbackProbeEngine(false, "rar"),
                    new FallbackProbeEngine(true, "7zz")));
            var zipPath = Path.Combine(tempDir, "diag.zip");
            await routedExporter.ExportAsync(zipPath);

            using var archive = ZipFile.OpenRead(zipPath);
            var summaryEntry = archive.GetEntry("diagnostics_summary.json");
            Assert(summaryEntry is not null, "诊断包必须生成 diagnostics_summary.json");
            using var summaryStream = summaryEntry!.Open();
            using var doc = JsonDocument.Parse(summaryStream);
            AssertEqual(false, doc.RootElement.GetProperty("RarEngineAvailable").GetBoolean());
            AssertEqual(true, doc.RootElement.GetProperty("SevenZipEngineAvailable").GetBoolean());
        }
        finally
        {
            if (Directory.Exists(tempDir))
            {
                Directory.Delete(tempDir, recursive: true);
            }
        }

        // 5. 仓库根 .gitignore 与主 .csproj 构建隔离检查。
        var gitignore = File.ReadAllText(Path.Combine(root, ".gitignore"));
        Assert(gitignore.Contains("[Bb]in/", StringComparison.Ordinal) &&
               gitignore.Contains("[Oo]bj/", StringComparison.Ordinal) &&
               gitignore.Contains(".artifacts/", StringComparison.Ordinal),
            ".gitignore 必须忽略构建与临时打包目录");

        var mainCsproj = File.ReadAllText(Path.Combine(root, "BatchCompress.Avalonia.csproj"));
        Assert(mainCsproj.Contains("<Compile Remove=\"BatchCompress.Avalonia\\**\\*.cs\" />", StringComparison.Ordinal) &&
               mainCsproj.Contains("<Compile Remove=\"BatchCompress.Avalonia.Tests\\**\\*.cs\" />", StringComparison.Ordinal),
            "主 csproj 必须隔离旧目录与测试工程源码");
    }
}
