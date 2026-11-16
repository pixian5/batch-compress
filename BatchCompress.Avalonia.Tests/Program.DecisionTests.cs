using System;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading.Tasks;
using BatchCompress.Avalonia;
using BatchCompress.Avalonia.Core.Models;
using BatchCompress.Avalonia.Core.Services;

// WorkBuddy, 2026-11-16：0.8.12——用户三项决定的回归守护：
// 1) Windows 不随包带 7-Zip，缺少时从官方渠道自动下载安装；
// 2) 命令行 --shutdown 真正调用系统关机命令；
// 3) 图形界面与命令行的默认压缩参数统一到 ArchiveDefaults。
internal static partial class Program
{
    private const string SampleReleaseJson = """
    {
      "assets": [
        {
          "name": "7z2606-x64.exe",
          "browser_download_url": "https://example.invalid/7z2606-x64.exe",
          "digest": "sha256:aaaa1111",
          "size": 100
        },
        {
          "name": "7z2606-x64.msi",
          "browser_download_url": "https://example.invalid/7z2606-x64.msi",
          "digest": "sha256:bbbb2222",
          "size": 200
        },
        {
          "name": "7z2606-linux-x64.tar.xz",
          "browser_download_url": "https://example.invalid/linux"
        }
      ]
    }
    """;

    private static Task TestUnifiedArchiveDefaults()
    {
        // 统一后的取值：标准压缩、3% 恢复记录、默认不分卷、单位 g。
        AssertEqual(3, ArchiveDefaults.DefaultCompressionLevel);
        AssertEqual(3, ArchiveDefaults.DefaultRecoveryRecordPercent);
        Assert(!ArchiveDefaults.DefaultEnableVolume, "默认应关闭分卷");
        AssertEqual("g", ArchiveDefaults.DefaultVolumeUnit);

        var options = new CommandLineOptions();
        AssertEqual(ArchiveDefaults.DefaultCompressionLevel, options.CompressionLevel);
        AssertEqual(ArchiveDefaults.DefaultRecoveryRecordPercent, options.RecoveryRecord);
        Assert(options.VolumeSize == null, "命令行默认不应带分卷大小");
        AssertEqual(ArchiveDefaults.DefaultVolumeUnit, options.VolumeUnit);

        // 界面字段必须引用同一份常量，不能再各写一套数字。
        var root = FindRepositoryRoot();
        var viewModel = File.ReadAllText(Path.Combine(root, "ViewModels", "MainWindowViewModel.cs"));
        Assert(viewModel.Contains("_compressionLevel = ArchiveDefaults.DefaultCompressionLevel", StringComparison.Ordinal),
            "界面压缩级别默认值未引用 ArchiveDefaults");
        Assert(viewModel.Contains("_enableVolume = ArchiveDefaults.DefaultEnableVolume", StringComparison.Ordinal),
            "界面分卷开关默认值未引用 ArchiveDefaults");
        Assert(viewModel.Contains("_recoveryRecordPercent = ArchiveDefaults.DefaultRecoveryRecordPercent", StringComparison.Ordinal),
            "界面恢复记录默认值未引用 ArchiveDefaults");

        // 页签状态类也持有一套默认值，切页时会覆盖字段初值，必须同样引用常量。
        Assert(viewModel.Contains("public int CompressionLevel { get; set; } = ArchiveDefaults.DefaultCompressionLevel", StringComparison.Ordinal),
            "页签状态的压缩级别默认值未引用 ArchiveDefaults");
        Assert(viewModel.Contains("public bool EnableVolume { get; set; } = ArchiveDefaults.DefaultEnableVolume", StringComparison.Ordinal),
            "页签状态的分卷开关默认值未引用 ArchiveDefaults");
        Assert(viewModel.Contains("public int RecoveryRecordPercent { get; set; } = ArchiveDefaults.DefaultRecoveryRecordPercent", StringComparison.Ordinal),
            "页签状态的恢复记录默认值未引用 ArchiveDefaults");
        Assert(viewModel.Contains("GUI_DEFAULTS", StringComparison.Ordinal),
            "界面启动时应把实际默认值写入运行日志，便于核对");

        return Task.CompletedTask;
    }

    private static Task TestSystemPowerCommands()
    {
        // Windows：一分钟延迟 + 可取消。
        var windowsShutdown = SystemPowerService.BuildShutdownCommand(OSPlatform.Windows);
        AssertEqual("shutdown", windowsShutdown.FileName);
        Assert(windowsShutdown.Arguments.SequenceEqual(new[] { "/s", "/t", "60" }),
            "Windows 关机参数应为 /s /t 60");
        Assert(SystemPowerService.BuildCancelCommand(OSPlatform.Windows).Arguments.SequenceEqual(new[] { "/a" }),
            "Windows 取消关机参数应为 /a");

        // Linux：一分钟延迟 + shutdown -c 取消。
        var linuxShutdown = SystemPowerService.BuildShutdownCommand(OSPlatform.Linux);
        AssertEqual("shutdown", linuxShutdown.FileName);
        Assert(linuxShutdown.Arguments.SequenceEqual(new[] { "-h", "+1" }),
            "Linux 关机参数应为 -h +1");
        Assert(SystemPowerService.BuildCancelCommand(OSPlatform.Linux).Arguments.SequenceEqual(new[] { "-c" }),
            "Linux 取消关机参数应为 -c");

        // macOS：/sbin/shutdown 需要 root，必须走 AppleScript，并保留 60 秒延迟。
        var macShutdown = SystemPowerService.BuildShutdownCommand(OSPlatform.OSX);
        AssertEqual("/usr/bin/osascript", macShutdown.FileName);
        Assert(macShutdown.Arguments.Contains("delay 60"), "macOS 关机脚本必须保留 60 秒延迟");
        Assert(macShutdown.Arguments.Any(argument => argument.Contains("shut down", StringComparison.Ordinal)),
            "macOS 关机脚本必须请求系统关机");

        // 命令行侧必须真正调用该服务，而不是只写日志。
        var runner = File.ReadAllText(Path.Combine(FindRepositoryRoot(), "HeadlessBatchRunner.cs"));
        Assert(runner.Contains("_powerService.ShutdownAsync()", StringComparison.Ordinal),
            "命令行关机必须调用系统关机服务");

        return Task.CompletedTask;
    }

    private static Task TestSevenZipReleaseJsonParsing()
    {
        Assert(SevenZipProvisioner.TryParseReleaseJson(SampleReleaseJson, out var assets),
            "官方发布 JSON 应能解析出安装包");
        AssertEqual(2, assets.Count);
        AssertEqual("7z2606-x64.msi", assets[0].Name);
        AssertEqual("bbbb2222", assets[0].Sha256);
        AssertEqual(200L, assets[0].Size);
        AssertEqual("7z2606-x64.exe", assets[1].Name);

        Assert(!SevenZipProvisioner.TryParseReleaseJson("{\"assets\":[]}", out var empty) || empty.Count == 0,
            "空资产列表不应产生安装包");
        Assert(!SevenZipProvisioner.TryParseReleaseJson("{}", out var missing) || missing.Count == 0,
            "缺少 assets 时不应产生安装包");

        return Task.CompletedTask;
    }

    private static Task TestSevenZipDownloadPageParsing()
    {
        const string html = """
        <p><a href="https://github.com/ip7z/7zip/releases/download/26.06/7z2606-x64.msi">Download</a></p>
        """;
        Assert(SevenZipProvisioner.TryParseDownloadPage(html, out var asset), "官方下载页应能解析出 MSI 链接");
        AssertEqual("7z2606-x64.msi", asset!.Name);
        Assert(asset.Url.EndsWith("/7z2606-x64.msi", StringComparison.Ordinal), "下载地址应以安装包名结尾");

        return Task.CompletedTask;
    }

    private static async Task TestSevenZipInstallLayoutAndChecksum()
    {
        var root = Path.Combine(Path.GetTempPath(), $"7zip-layout-{Guid.NewGuid():N}");
        var nested = Path.Combine(root, "ProgramFiles", "7-Zip");
        Directory.CreateDirectory(nested);

        try
        {
            File.WriteAllText(Path.Combine(nested, "7z.exe"), "stub");
            File.WriteAllText(Path.Combine(nested, "7z.dll"), "stub");
            File.WriteAllText(Path.Combine(nested, "License.txt"), "license");

            var executable = SevenZipProvisioner.NormalizeInstallLayout(root);
            Assert(executable != null, "嵌套目录中的 7z.exe 应被归位到安装根目录");
            AssertEqual(Path.Combine(root, "7z.exe"), executable);
            Assert(File.Exists(Path.Combine(root, "7z.dll")), "7z.dll 必须一并归位，否则 WIM 等格式不可用");

            // 只有 exe、没有 dll 时不能算安装成功。
            var incomplete = Path.Combine(root, "incomplete");
            Directory.CreateDirectory(incomplete);
            File.WriteAllText(Path.Combine(incomplete, "7z.exe"), "stub");
            Assert(SevenZipProvisioner.NormalizeInstallLayout(incomplete) == null,
                "缺少 7z.dll 时不应判定安装成功");

            // 校验值必须与标准 SHA-256 一致。
            var sample = Path.Combine(root, "sample.bin");
            await File.WriteAllTextAsync(sample, "abc");
            var hash = await SevenZipProvisioner.ComputeSha256Async(sample, default);
            AssertEqual("ba7816bf8f01cfea414140de5dae2223b00361a396177a9cb410ff61f20015ad", hash);

            // 自动安装入口必须接到两个入口：界面启动与命令行执行。
            var repositoryRoot = FindRepositoryRoot();
            var tooling = Path.Combine(repositoryRoot, "ViewModels", "MainWindowViewModel.Tooling.cs");
            Assert(File.Exists(tooling), "缺少界面侧 7-Zip 自动安装分部类");
            Assert(File.ReadAllText(tooling).Contains("SevenZipProvisioner.EnsureAsync", StringComparison.Ordinal),
                "界面侧未调用自动安装服务");
            Assert(File.ReadAllText(Path.Combine(repositoryRoot, "Views", "MainWindow.axaml.cs"))
                    .Contains("EnsureArchiveToolingAsync", StringComparison.Ordinal),
                "界面加载后未触发 7-Zip 检查");
            Assert(File.ReadAllText(Path.Combine(repositoryRoot, "HeadlessBatchRunner.cs"))
                    .Contains("EnsureSevenZipProvisionedAsync", StringComparison.Ordinal),
                "命令行执行前未触发 7-Zip 检查");
        }
        finally
        {
            if (Directory.Exists(root))
            {
                Directory.Delete(root, recursive: true);
            }
        }
    }
}
