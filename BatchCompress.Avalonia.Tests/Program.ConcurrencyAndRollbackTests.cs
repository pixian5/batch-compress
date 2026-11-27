using System.Collections.Generic;
using BatchCompress.Avalonia.Core.Interfaces;
using BatchCompress.Avalonia.Core.Models;
using BatchCompress.Avalonia.Core.Services;
using BatchCompress.Avalonia.Localization;
using BatchCompress.Avalonia.ViewModels;

// Gemini 4 Hydrogen, 2026-11-27：批次 46——从 Program.PipelineTests.cs 拆出的并发执行、失败/取消回滚、子进度流与按卷大小写敏感一致性测试。
internal static partial class Program
{
    // 真并发（峰值==并发度）、全部计数无丢失、串行路径不受并发选项影响。
    private static async Task TestConcurrentBatchExecution()
    {
        var root = Path.Combine(Path.GetTempPath(), $"bc-concurrent-{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);
        try
        {
            // 压缩：5 个来源、并发 3——引擎在途峰值必须恰为 3，全部成功、计数无丢失。
            var engine = new ConcurrentTrackingEngine();
            var sources = new List<string>();
            for (var i = 0; i < 5; i++)
            {
                var dir = Path.Combine(root, $"src{i}");
                Directory.CreateDirectory(dir);
                File.WriteAllText(Path.Combine(dir, "a.txt"), "a");
                sources.Add(dir);
            }

            var snapshots = new List<OperationProgressInfo>();
            await new BatchOperationService(engine, new RecordingSystemIntegration()).BatchCompressAsync(
                sources,
                new BatchOperationOptions
                {
                    OutputPath = Path.Combine(root, "out"),
                    Extension = "7z",
                    ExistingFileMode = ExistingFileMode.Overwrite,
                    MaxConcurrency = 3
                },
                new SnapshotProgress(snapshots), CancellationToken.None);

            AssertEqual(5, engine.CompressionInputs.Count);
            AssertEqual(3, engine.PeakInFlight);
            AssertEqual(5, snapshots.Last().SuccessCount);
            AssertEqual(0, snapshots.Last().FailCount);

            // 解压：5 个归档、并发 3——同样断言峰值与计数。
            engine = new ConcurrentTrackingEngine();
            var archives = new List<FileEntry>();
            for (var i = 0; i < 5; i++)
            {
                var archive = Path.Combine(root, $"arc{i}.7z");
                File.WriteAllText(archive, "x");
                archives.Add(new FileEntry { FilePath = archive, FileSize = 1 });
            }

            snapshots = new List<OperationProgressInfo>();
            await new BatchOperationService(engine, new RecordingSystemIntegration()).BatchDecompressAsync(
                archives,
                new BatchOperationOptions
                {
                    OutputPath = Path.Combine(root, "ex"),
                    Extension = "7z",
                    ExistingFileMode = ExistingFileMode.Overwrite,
                    MaxConcurrency = 3
                },
                new SnapshotProgress(snapshots), CancellationToken.None);

            AssertEqual(5, engine.ExtractionInputs.Count);
            AssertEqual(3, engine.PeakInFlight);
            AssertEqual(5, snapshots.Last().SuccessCount);

            // 串行路径回归：并发度 1 时峰值必须为 1（不进入并发分支）。
            engine = new ConcurrentTrackingEngine();
            snapshots = new List<OperationProgressInfo>();
            await new BatchOperationService(engine, new RecordingSystemIntegration()).BatchCompressAsync(
                sources,
                new BatchOperationOptions
                {
                    OutputPath = Path.Combine(root, "out"),
                    Extension = "7z",
                    ExistingFileMode = ExistingFileMode.Overwrite,
                    MaxConcurrency = 1
                },
                new SnapshotProgress(snapshots), CancellationToken.None);
            AssertEqual(1, engine.PeakInFlight);
            AssertEqual(5, snapshots.Last().SuccessCount);
        }
        finally
        {
            Directory.Delete(root, true);
        }
    }

    // GLM-5.3, 2026-11-04：批次 5——并发下的取消传播与大小上限：
    // 外部取消即时中止全部并发任务且绝不关机；达限只停排队任务、在途自然完成、
    // SizeLimitReached 恰好一次、非取消结束关机恰好一次。
    private static async Task TestConcurrentCancellationAndLimit()
    {
        var L = LocalizationService.Instance.Strings;
        var root = Path.Combine(Path.GetTempPath(), $"bc-concurrent-limit-{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);
        try
        {
            // 1) 并发执行中外部取消：所有已启动任务抛取消异常聚合传播、不触发关机。
            var cancelEngine = new ScriptedArchiveEngine { CancelAfterMilliseconds = 400 };
            var cancelSources = new List<string>();
            for (var i = 0; i < 3; i++)
            {
                var dir = Path.Combine(root, $"cancel{i}");
                Directory.CreateDirectory(dir);
                File.WriteAllText(Path.Combine(dir, "a.txt"), "a");
                cancelSources.Add(dir);
            }

            using var cts = new CancellationTokenSource();
            cts.CancelAfter(50);
            var cancelSystem = new RecordingSystemIntegration();
            await AssertThrowsAsync<OperationCanceledException>(
                new BatchOperationService(cancelEngine, cancelSystem).BatchCompressAsync(
                    cancelSources,
                    new BatchOperationOptions
                    {
                        OutputPath = Path.Combine(root, "cancel-out"),
                        Extension = "7z",
                        ExistingFileMode = ExistingFileMode.Overwrite,
                        MaxConcurrency = 3,
                        ShutdownAfterComplete = true
                    },
                    new SnapshotProgress([]), cts.Token));
            AssertEqual(3, cancelEngine.CompressionInputs.Count);
            AssertEqual(0, cancelSystem.ShutdownCalls);

            // 2) 大小上限：4 个来源、并发 2、每项真实写出 1MB、上限 0.0005GB——
            //    首个完成项即达限；两个初始槽位任务自然完成，剩余排队任务全部跳过。
            var limitEngine = new ConcurrentTrackingEngine { DelayMilliseconds = 60, WriteRealOutput = true };
            var limitSources = new List<string>();
            for (var i = 0; i < 4; i++)
            {
                var dir = Path.Combine(root, $"limit{i}");
                Directory.CreateDirectory(dir);
                File.WriteAllText(Path.Combine(dir, "a.txt"), "a");
                limitSources.Add(dir);
            }

            var snapshots = new List<OperationProgressInfo>();
            var limitSystem = new RecordingSystemIntegration();
            await new BatchOperationService(limitEngine, limitSystem).BatchCompressAsync(
                limitSources,
                new BatchOperationOptions
                {
                    OutputPath = Path.Combine(root, "limit-out"),
                    Extension = "7z",
                    ExistingFileMode = ExistingFileMode.Overwrite,
                    MaxConcurrency = 2,
                    MaxSizeGB = 0.0005,
                    ShutdownAfterComplete = true
                },
                new CloningProgress(snapshots), CancellationToken.None);

            AssertEqual(2, limitEngine.CompressionInputs.Count);
            AssertEqual(2, snapshots.Last().SuccessCount);
            AssertEqual(0, snapshots.Last().FailCount);
            Assert(snapshots.Count(info => info.Message == L.SizeLimitReached) == 1,
                "SizeLimitReached 必须恰好上报一次");
            AssertEqual(1, limitSystem.ShutdownCalls);
        }
        finally
        {
            Directory.Delete(root, true);
        }
    }

    // Antigravity, 2026-11-07：批次 13——压缩失败时回滚本次新增的在途未完成文件与分卷残片。
    private static async Task TestFailedCompressionRollback()
    {
        var root = Path.Combine(Path.GetTempPath(), $"batch-compress-comp-rollback-{Guid.NewGuid():N}");
        var source = Path.Combine(root, "source.txt");
        var outputDir = Path.Combine(root, "output");
        Directory.CreateDirectory(root);
        Directory.CreateDirectory(outputDir);
        File.WriteAllText(source, "source data");
        var keepFile = Path.Combine(outputDir, "keep.txt");
        File.WriteAllText(keepFile, "keep me");

        try
        {
            var engine = new PartialFailureArchiveEngine();
            var snapshots = new List<OperationProgressInfo>();
            await new BatchOperationService(engine, new TestSystemIntegration()).BatchCompressAsync(
                [source],
                new BatchOperationOptions { OutputPath = outputDir, Extension = "7z" },
                new SnapshotProgress(snapshots),
                CancellationToken.None);

            AssertEqual(1, snapshots.Last().FailCount);
            Assert(File.Exists(keepFile), "压缩失败不得删除输出目录中原有无关文件");

            var targetArchive = Path.Combine(outputDir, "source.txt.7z");
            Assert(!File.Exists(targetArchive), "压缩失败必须清理本次生成的不完整归档文件");
            Assert(!File.Exists(targetArchive + ".001"), "压缩失败必须清理本次生成的分卷文件 001");
            Assert(!File.Exists(targetArchive + ".002"), "压缩失败必须清理本次生成的分卷文件 002");
        }
        finally
        {
            if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
        }
    }

    // WorkBuddy, 2026-11-17：0.8.15——覆盖失败必须恢复旧归档，不能新旧全失。
    private static async Task TestOverwriteFailureKeepsExistingArchive()
    {
        var root = Path.Combine(Path.GetTempPath(), $"batch-compress-overwrite-{Guid.NewGuid():N}");
        var source = Path.Combine(root, "source.txt");
        var outputDir = Path.Combine(root, "output");
        Directory.CreateDirectory(root);
        Directory.CreateDirectory(outputDir);
        File.WriteAllText(source, "source data");

        var targetArchive = Path.Combine(outputDir, "source.txt.7z");
        File.WriteAllText(targetArchive, "old archive");

        try
        {
            var engine = new PartialFailureArchiveEngine();
            var snapshots = new List<OperationProgressInfo>();
            await new BatchOperationService(engine, new TestSystemIntegration()).BatchCompressAsync(
                [source],
                new BatchOperationOptions
                {
                    OutputPath = outputDir,
                    Extension = "7z",
                    ExistingFileMode = ExistingFileMode.Overwrite
                },
                new SnapshotProgress(snapshots),
                CancellationToken.None);

            AssertEqual(1, snapshots.Last().FailCount);
            Assert(File.Exists(targetArchive), "覆盖失败后旧归档必须存在");
            AssertEqual("old archive", File.ReadAllText(targetArchive));
            Assert(!File.Exists(targetArchive + ".001"), "覆盖失败必须清理新生成的分卷");
            Assert(!File.Exists(targetArchive + ".002"), "覆盖失败必须清理新生成的分卷");
            Assert(!File.Exists(targetArchive + ".batchcompress-backup"), "覆盖失败后不应残留备份");
        }
        finally
        {
            if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
        }
    }

    // Antigravity, 2026-11-07：批次 13——压缩中途取消时可靠清理在途未完成文件与分卷残片。
    private static async Task TestCancelledCompressionRollback()
    {
        var root = Path.Combine(Path.GetTempPath(), $"batch-compress-comp-cancel-{Guid.NewGuid():N}");
        var source = Path.Combine(root, "source.txt");
        var outputDir = Path.Combine(root, "output");
        Directory.CreateDirectory(root);
        Directory.CreateDirectory(outputDir);
        File.WriteAllText(source, "source data");
        var keepFile = Path.Combine(outputDir, "keep.txt");
        File.WriteAllText(keepFile, "keep me");

        try
        {
            var engine = new CancellingFailureArchiveEngine();
            using var cts = new CancellationTokenSource();
            cts.CancelAfter(50);

            await AssertThrowsAsync<OperationCanceledException>(
                new BatchOperationService(engine, new TestSystemIntegration()).BatchCompressAsync(
                    [source],
                    new BatchOperationOptions { OutputPath = outputDir, Extension = "7z" },
                    new SnapshotProgress([]),
                    cts.Token));

            Assert(File.Exists(keepFile), "压缩取消不得删除输出目录中原有无关文件");

            var targetArchive = Path.Combine(outputDir, "source.txt.7z");
            Assert(!File.Exists(targetArchive), "压缩取消必须清理在途未完成归档文件");
            Assert(!File.Exists(targetArchive + ".001"), "压缩取消必须清理在途分卷文件");
        }
        finally
        {
            if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
        }
    }

    // Antigravity, 2026-11-07：批次 13——解压中途取消时可靠清理在途部分解压文件与新建目录。
    private static async Task TestCancelledExtractionRollback()
    {
        var root = Path.Combine(Path.GetTempPath(), $"batch-compress-ext-cancel-{Guid.NewGuid():N}");
        var archive = Path.Combine(root, "archive.7z");
        var outputDir = Path.Combine(root, "output");
        Directory.CreateDirectory(root);
        Directory.CreateDirectory(outputDir);
        File.WriteAllText(archive, "placeholder");
        var keepFile = Path.Combine(outputDir, "keep.txt");
        File.WriteAllText(keepFile, "keep me");

        try
        {
            var engine = new CancellingFailureArchiveEngine();
            using var cts = new CancellationTokenSource();
            cts.CancelAfter(50);

            await AssertThrowsAsync<OperationCanceledException>(
                new BatchOperationService(engine, new TestSystemIntegration()).BatchDecompressAsync(
                    [new FileEntry { FilePath = archive, FileSize = 100 }],
                    new BatchOperationOptions { OutputPath = outputDir, Extension = "7z" },
                    new SnapshotProgress([]),
                    cts.Token));

            Assert(File.Exists(keepFile), "解压取消不得删除原有文件");
            Assert(!File.Exists(Path.Combine(outputDir, "partial-cancel.bin")), "解压取消必须清理未完成解压文件");
            Assert(!Directory.Exists(Path.Combine(outputDir, "partial-cancel")), "解压取消必须清理新建目录");
        }
        finally
        {
            if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
        }
    }

    // Antigravity, 2026-11-08：批次 17（P3-11）——模拟输出行进度回调的测试引擎。
    private sealed class StreamingMockArchiveEngine : IArchiveEngine
    {
        public bool IsAvailable() => true;

        public Task<ArchiveResult> CompressAsync(string input, string output, ArchiveOptions options, CancellationToken cancellationToken = default)
        {
            // 触发 35% 与 80% 进度流
            options.OutputLineHandler?.Invoke("  35% 1 + test.txt");
            options.OutputLineHandler?.Invoke("  80% 1 + test.txt");
            return Task.FromResult(new ArchiveResult
            {
                Success = true,
                ExitCode = 0,
                StandardOutput = "Everything is Ok\n"
            });
        }

        public Task<ArchiveResult> ExtractAsync(string archivePath, string outputDir, ArchiveOptions options, CancellationToken cancellationToken = default)
        {
            options.OutputLineHandler?.Invoke("  50%");
            return Task.FromResult(new ArchiveResult
            {
                Success = true,
                ExitCode = 0,
                StandardOutput = "Everything is Ok\n"
            });
        }
    }

    // Antigravity, 2026-11-08：批次 17（P3-11）——验证管线在归档引擎调用期间实时解析并上报子进度。
    private static async Task TestPipelineSubProgressStreaming()
    {
        var root = Path.Combine(Path.GetTempPath(), $"bc-subprog-{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);
        var src = Path.Combine(root, "input.txt");
        File.WriteAllText(src, "test content");

        try
        {
            var progressSnapshots = new List<OperationProgressInfo>();
            var engine = new StreamingMockArchiveEngine();
            var service = new BatchOperationService(engine, new TestSystemIntegration());
            var progress = new CloningProgress(progressSnapshots);

            await service.BatchCompressAsync(
                [src],
                new BatchOperationOptions { Extension = "7z" },
                progress,
                CancellationToken.None);

            Assert(progressSnapshots.Any(p => p.SubProgressPercent == 35), "管线必须捕获并上报引擎 35% 子进度");
            Assert(progressSnapshots.Any(p => p.SubProgressPercent == 80), "管线必须捕获并上报引擎 80% 子进度");
            Assert(progressSnapshots.Last().SubProgressPercent == null, "条目完成后子进度必须重置为 null");
            AssertEqual(1, progressSnapshots.Last().SuccessCount);
        }
        finally
        {
            if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
        }
    }

    // WorkBuddy, 2026-11-16：0.8.13——串行路径曾把同一个可变进度对象交给异步回调，
    // 第二项开始后会改写第一项的快照，逐项日志因此可能张冠李戴。
    private static async Task TestSequentialProgressSnapshotsAreImmutable()
    {
        var root = Path.Combine(Path.GetTempPath(), $"sequential-snapshot-{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);
        var first = Path.Combine(root, "first.txt");
        var second = Path.Combine(root, "second.txt");
        File.WriteAllText(first, "first");
        File.WriteAllText(second, "second");

        try
        {
            var snapshots = new List<OperationProgressInfo>();
            var service = new BatchOperationService(new TestArchiveEngine(), new TestSystemIntegration());
            await service.BatchCompressAsync(
                [first, second],
                new BatchOperationOptions
                {
                    OutputPath = Path.Combine(root, "out"),
                    Extension = "7z",
                    ExistingFileMode = ExistingFileMode.Overwrite
                },
                new CloningProgress(snapshots),
                CancellationToken.None);

            var firstStarted = snapshots.First(snapshot => !string.IsNullOrEmpty(snapshot.CurrentFile));
            AssertEqual("first.txt", firstStarted.CurrentFile);
            Assert(snapshots.Any(snapshot => snapshot.CurrentFile == "second.txt"),
                "第二项也必须有自己的进度快照");
        }
        finally
        {
            if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
        }
    }

    // Gemini 4 Neon, 2026-11-26 / 2026-11-27：批次 34 & 39——按卷大小写敏感能力验证解压失败回滚。
    private static async Task TestFailedExtractionRollbackCaseSensitivity()
    {
        await RunCaseVariantRollbackScenario(Path.GetTempPath());

        var caseSensitiveDir = ResolveCaseSensitiveBaseDirectory();
        if (caseSensitiveDir != null &&
            !string.Equals(Path.GetFullPath(caseSensitiveDir), Path.GetFullPath(Path.GetTempPath()), StringComparison.Ordinal))
        {
            await RunCaseVariantRollbackScenario(caseSensitiveDir);
        }
    }

    private static async Task RunCaseVariantRollbackScenario(string baseDir)
    {
        var root = Path.Combine(baseDir, $"batch-compress-extraction-case-{Guid.NewGuid():N}");
        var archive = Path.Combine(root, "pack.7z");
        var outDir = Path.Combine(root, "out");
        Directory.CreateDirectory(outDir);
        File.WriteAllText(archive, "archive placeholder");
        File.WriteAllText(Path.Combine(outDir, "readme.txt"), "existing");

        try
        {
            var isCaseSensitive = FileSystemPathIdentity.IsCaseSensitive(outDir);
            var engine = new CaseVariantFailureArchiveEngine();
            var snapshots = new List<OperationProgressInfo>();
            await new BatchOperationService(engine, new TestSystemIntegration()).BatchDecompressAsync(
                [new FileEntry { FilePath = archive, FileSize = new FileInfo(archive).Length }],
                new BatchOperationOptions { OutputPath = outDir, Extension = "7z" },
                new SnapshotProgress(snapshots),
                CancellationToken.None);

            AssertEqual(1, snapshots.Last().FailCount);
            Assert(File.Exists(Path.Combine(outDir, "readme.txt")), "解压失败回滚不得误删原有文件");
            Assert(!File.Exists(Path.Combine(outDir, "partial.tmp")), "真正新增的残片文件必须被清理");
            if (isCaseSensitive)
            {
                Assert(!File.Exists(Path.Combine(outDir, "Readme.txt")), "大小写敏感卷下应清理新增的大小写不同残片");
            }
        }
        finally
        {
            if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
        }
    }

    // Gemini 4 Neon, 2026-11-27：批次 39——验证按卷检测大小写敏感能力及预检行为。
    private static Task TestVolumeCaseSensitivityDetection()
    {
        var tempPath = Path.GetTempPath();
        var expectedDefault = !OperatingSystem.IsWindows() && !OperatingSystem.IsMacOS();
        AssertEqual(expectedDefault, FileSystemPathIdentity.IsCaseSensitive(tempPath));
        AssertEqual(
            expectedDefault ? StringComparer.Ordinal : StringComparer.OrdinalIgnoreCase,
            FileSystemPathIdentity.GetComparer(tempPath));

        var caseSensitiveDir = ResolveCaseSensitiveBaseDirectory();
        if (caseSensitiveDir != null)
        {
            Assert(FileSystemPathIdentity.IsCaseSensitive(caseSensitiveDir), "真实大小写敏感卷必须被检测为 true");
            AssertEqual(StringComparer.Ordinal, FileSystemPathIdentity.GetComparer(caseSensitiveDir));

            var subRoot = Path.Combine(caseSensitiveDir, $"bc-case-preflight-{Guid.NewGuid():N}");
            var outDir = Path.Combine(subRoot, "out");
            Directory.CreateDirectory(subRoot);
            Directory.CreateDirectory(outDir);
            try
            {
                var lower = Path.Combine(subRoot, "item.txt");
                var upper = Path.Combine(subRoot, "Item.txt");
                File.WriteAllText(lower, "lower");
                File.WriteAllText(upper, "upper");

                var preflight = BatchPreflightService.InspectCompression(
                    [lower, upper],
                    new BatchOperationOptions { OutputPath = outDir, Extension = "7z" });
                Assert(preflight.CanExecute, "大小写敏感卷上 item.txt 与 Item.txt 输出到 item.txt.7z / Item.txt.7z 不应误报冲突");
            }
            finally
            {
                if (Directory.Exists(subRoot)) Directory.Delete(subRoot, recursive: true);
            }
        }

        return Task.CompletedTask;
    }

    // Gemini 4 Hydrogen, 2026-11-27：批次 43——全仓路径比较按卷大小写敏感一致性回归测试。
    private static async Task TestCrossVolumePathIdentityConsistency()
    {
        // 1. VolumeAwarePathComparer 在默认卷下应符合该卷的大小写敏感规则。
        var tempRoot = Path.GetTempPath();
        var defaultSensitive = FileSystemPathIdentity.IsCaseSensitive(tempRoot);
        var pathA = Path.Combine(tempRoot, "Alpha.txt");
        var patha = Path.Combine(tempRoot, "alpha.txt");
        AssertEqual(!defaultSensitive, VolumeAwarePathComparer.Instance.Equals(pathA, patha));

        // 2. MainWindow.axaml.cs 的 PathsMatch 必须走 FileSystemPathIdentity，而非硬编码平台判断。
        var repoRoot = FindRepositoryRoot();
        var mainWindowCode = File.ReadAllText(Path.Combine(repoRoot, "Views", "MainWindow.axaml.cs"));
        Assert(mainWindowCode.Contains("FileSystemPathIdentity.GetComparer", StringComparison.Ordinal),
            "MainWindow.axaml.cs 应改用 FileSystemPathIdentity.GetComparer");

        // 3. 在真实大小写敏感卷上验证解压去重、目录扫描与失败回滚都区分大小写变体。
        var caseSensitiveDir = ResolveCaseSensitiveBaseDirectory();
        if (caseSensitiveDir == null)
        {
            Console.WriteLine("SKIP 跨卷路径比较深测: 需要区分大小写的文件系统（可设置 BATCHCOMPRESS_CASE_SENSITIVE_DIR）");
            return;
        }

        var root = Path.Combine(caseSensitiveDir, $"bc-vol-consistency-{Guid.NewGuid():N}");
        var srcDir = Path.Combine(root, "src");
        var outDir = Path.Combine(root, "out");
        Directory.CreateDirectory(srcDir);
        Directory.CreateDirectory(outDir);
        try
        {
            var lowerArchive = Path.Combine(srcDir, "pack.7z");
            var upperArchive = Path.Combine(srcDir, "Pack.7z");
            File.WriteAllText(lowerArchive, "lower");
            File.WriteAllText(upperArchive, "upper");

            var service = new BatchOperationService(new ScriptedArchiveEngine(), new RecordingSystemIntegration());
            var scanned = service.LoadArchivesFromFolder(srcDir, "7z", skipProcessed: false);
            AssertEqual(2, scanned.Count);

            var engine = new ScriptedArchiveEngine();
            var snapshots = new List<OperationProgressInfo>();
            await new BatchOperationService(engine, new RecordingSystemIntegration()).BatchDecompressAsync(
                [
                    new FileEntry { FilePath = lowerArchive, FileSize = 5 },
                    new FileEntry { FilePath = upperArchive, FileSize = 5 }
                ],
                new BatchOperationOptions
                {
                    OutputPath = outDir,
                    Extension = "7z",
                    ExistingFileMode = ExistingFileMode.Overwrite
                },
                new SnapshotProgress(snapshots),
                CancellationToken.None);

            // 大小写敏感卷下 pack.7z 与 Pack.7z 是两个不同文件，不能被 _processedArchives 误跳过。
            AssertEqual(2, engine.ExtractionInputs.Count);
            AssertEqual(0, snapshots.Last().IgnoreCount);

            // FailedItemRetryList 在大小写敏感卷上也必须保留两个大小写变体。
            var retry = new FailedItemRetryList();
            retry.Record(new OperationProgressInfo { FailCount = 1, CurrentSourcePath = lowerArchive });
            retry.Record(new OperationProgressInfo { FailCount = 2, CurrentSourcePath = upperArchive });
            AssertEqual(2, retry.GetFailedPaths().Count);
        }
        finally
        {
            if (Directory.Exists(root))
            {
                Directory.Delete(root, recursive: true);
            }
        }
    }

    private static string? ResolveCaseSensitiveBaseDirectory()
    {
        var configured = Environment.GetEnvironmentVariable("BATCHCOMPRESS_CASE_SENSITIVE_DIR");
        if (!string.IsNullOrWhiteSpace(configured) &&
            Directory.Exists(configured) &&
            FileSystemPathIdentity.IsCaseSensitive(configured))
        {
            return configured;
        }

        if (OperatingSystem.IsLinux() && FileSystemPathIdentity.IsCaseSensitive(Path.GetTempPath()))
        {
            return Path.GetTempPath();
        }

        return null;
    }

    private sealed class CaseVariantFailureArchiveEngine : IArchiveEngine
    {
        public bool IsAvailable() => true;

        public Task<ArchiveResult> CompressAsync(
            string input,
            string output,
            ArchiveOptions options,
            CancellationToken cancellationToken = default) =>
            Task.FromResult(new ArchiveResult { Success = true });

        public Task<ArchiveResult> ExtractAsync(
            string archivePath,
            string outputDir,
            ArchiveOptions options,
            CancellationToken cancellationToken = default)
        {
            File.WriteAllText(Path.Combine(outputDir, "Readme.txt"), "overwritten");
            File.WriteAllText(Path.Combine(outputDir, "partial.tmp"), "partial");
            return Task.FromResult(new ArchiveResult
            {
                Success = false,
                ExitCode = 2,
                ErrorMessage = "mock fail"
            });
        }
    }
}
