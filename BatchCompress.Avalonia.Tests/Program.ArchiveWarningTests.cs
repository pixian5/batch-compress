using System.Collections.Generic;
using BatchCompress.Avalonia.Core.Interfaces;
using BatchCompress.Avalonia.Core.Models;
using BatchCompress.Avalonia.Core.Services;
using BatchCompress.Avalonia.Localization;

internal static partial class Program
{
    // Claude Opus 5.5, 2026-11-20：0.8.20——归档程序返回码 1（警告）时不得删除/移动来源。
    // 复现场景：源文件无读权限，7zz 返回 1 生成空归档，7zz t 校验仍通过，旧逻辑会删掉源文件。
    private static async Task TestArchiveWarningKeepsSources()
    {
        var root = Path.Combine(Path.GetTempPath(), $"batch-compress-warning-{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);
        try
        {
            var strings = LocalizationService.Instance.Strings;

            // 场景 1/2：压缩删除源，串行与并发两条路径都要拦截。
            foreach (var concurrency in new[] { 1, 2 })
            {
                var source = Path.Combine(root, $"del-{concurrency}.txt");
                File.WriteAllText(source, "important");
                var snapshots = new List<OperationProgressInfo>();
                await new BatchOperationService(new WarningArchiveEngine(), new TestSystemIntegration())
                    .BatchCompressAsync(
                        [source],
                        new BatchOperationOptions
                        {
                            OutputPath = Path.Combine(root, $"out-{concurrency}"),
                            Extension = "7z",
                            ExistingFileMode = ExistingFileMode.Overwrite,
                            DeleteSourceAfter = true,
                            MaxConcurrency = concurrency
                        },
                        new SnapshotProgress(snapshots),
                        CancellationToken.None);
                var final = snapshots.Last();
                Assert(File.Exists(source), $"并发度 {concurrency}：归档有警告时不得删除来源");
                AssertEqual(1, final.SuccessCount);
                AssertEqual(1, final.PostProcessFailCount);
                Assert(final.IsError, $"并发度 {concurrency}：有警告的条目必须标记为错误提示");
                Assert(final.Message.Contains(Path.GetFileName(source), StringComparison.Ordinal),
                    "提示中必须包含条目名");
            }

            // 场景 3：压缩移动源同样拦截，来源留在原处，不创建【已压缩】目录内的副本。
            var moveSource = Path.Combine(root, "mv-src");
            Directory.CreateDirectory(moveSource);
            File.WriteAllText(Path.Combine(moveSource, "a.txt"), "a");
            var moveSnapshots = new List<OperationProgressInfo>();
            await new BatchOperationService(new WarningArchiveEngine(), new TestSystemIntegration())
                .BatchCompressAsync(
                    [moveSource],
                    new BatchOperationOptions
                    {
                        OutputPath = Path.Combine(root, "mv-out"),
                        Extension = "7z",
                        ExistingFileMode = ExistingFileMode.Overwrite,
                        MoveSourceAfter = true
                    },
                    new SnapshotProgress(moveSnapshots),
                    CancellationToken.None);
            Assert(Directory.Exists(moveSource), "归档有警告时不得移动来源");
            AssertEqual(1, moveSnapshots.Last().PostProcessFailCount);

            // 场景 4：解压删除归档同样拦截。
            var archive = Path.Combine(root, "pack.7z");
            File.WriteAllText(archive, "archive");
            var extractSnapshots = new List<OperationProgressInfo>();
            await new BatchOperationService(new WarningArchiveEngine(), new TestSystemIntegration())
                .BatchDecompressAsync(
                    [new FileEntry { FilePath = archive, FileSize = new FileInfo(archive).Length }],
                    new BatchOperationOptions
                    {
                        OutputPath = Path.Combine(root, "extract-out"),
                        ExistingFileMode = ExistingFileMode.Overwrite,
                        DeleteSourceAfter = true
                    },
                    new SnapshotProgress(extractSnapshots),
                    CancellationToken.None);
            Assert(File.Exists(archive), "解压有警告时不得删除归档");
            AssertEqual(1, extractSnapshots.Last().PostProcessFailCount);

            // 场景 5：不开后处理时，有警告的条目仍计成功，不额外记后处理失败。
            var plainSource = Path.Combine(root, "plain.txt");
            File.WriteAllText(plainSource, "plain");
            var plainSnapshots = new List<OperationProgressInfo>();
            await new BatchOperationService(new WarningArchiveEngine(), new TestSystemIntegration())
                .BatchCompressAsync(
                    [plainSource],
                    new BatchOperationOptions
                    {
                        OutputPath = Path.Combine(root, "plain-out"),
                        Extension = "7z",
                        ExistingFileMode = ExistingFileMode.Overwrite
                    },
                    new SnapshotProgress(plainSnapshots),
                    CancellationToken.None);
            AssertEqual(1, plainSnapshots.Last().SuccessCount);
            AssertEqual(0, plainSnapshots.Last().PostProcessFailCount);
            Assert(!string.IsNullOrWhiteSpace(strings.PostProcessSkippedArchiveWarning), "警告词条不能为空");

            // 场景 6：真实 7zz（仅 macOS）——无读权限文件必须产生 HasWarnings，且校验阶段不能把它抹掉。
            if (OperatingSystem.IsMacOS())
            {
                var executable = Path.Combine(AppContext.BaseDirectory, "tools", "7zip", "macos", "7zz");
                var locked = Path.Combine(root, "locked.txt");
                File.WriteAllText(locked, "secret");
                File.SetUnixFileMode(locked, UnixFileMode.None);
                try
                {
                    var result = await new SevenZipArchiveEngine(executable).CompressAsync(
                        locked,
                        Path.Combine(root, "locked.txt.7z"),
                        new ArchiveOptions { ArchiveFormat = "7z", TestArchive = true });
                    Assert(result.Success, "7zz 返回码 1 仍视为归档已生成");
                    Assert(result.HasWarnings, "无读权限文件必须标记 HasWarnings（校验阶段不得清除）");
                    AssertEqual(SevenZipExitCodes.Warning, result.ExitCode);
                }
                finally
                {
                    File.SetUnixFileMode(locked, UnixFileMode.UserRead | UnixFileMode.UserWrite);
                }
            }
            else
            {
                Console.WriteLine("SKIP 真实 7zz 警告返回码: 当前不是 macOS");
            }
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    // 模拟 7zz/RAR 返回码 1：归档“成功”生成但带警告，并真实写出输出文件。
    private sealed class WarningArchiveEngine : IArchiveEngine
    {
        public bool IsAvailable() => true;

        public Task<ArchiveResult> CompressAsync(
            string input,
            string output,
            ArchiveOptions options,
            CancellationToken cancellationToken = default)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(output)!);
            File.WriteAllText(output, "partial archive");
            return Task.FromResult(new ArchiveResult { Success = true, HasWarnings = true, ExitCode = 1 });
        }

        public Task<ArchiveResult> ExtractAsync(
            string archivePath,
            string outputDir,
            ArchiveOptions options,
            CancellationToken cancellationToken = default)
        {
            Directory.CreateDirectory(outputDir);
            File.WriteAllText(Path.Combine(outputDir, "part.txt"), "partial");
            return Task.FromResult(new ArchiveResult { Success = true, HasWarnings = true, ExitCode = 1 });
        }
    }
}
