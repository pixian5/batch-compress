using System.Collections.Generic;
using System.IO.Compression;
using CompressionLevel = BatchCompress.Avalonia.Core.Interfaces.CompressionLevel;
using System.Text;
using System.Text.RegularExpressions;
using BatchCompress.Avalonia.Core.Interfaces;
using BatchCompress.Avalonia.Core.Models;
using BatchCompress.Avalonia.Core.Services;
using BatchCompress.Avalonia.Localization;
using BatchCompress.Avalonia.ViewModels;

internal static partial class Program
{
    // 并发批处理、管道流转、取消与失败回滚测试


    private static async Task TestCancellation()
    {
        var (executable, arguments) = GetSleepCommand();
        var runner = new WinRarProcessRunner(executable);
        using var cancellation = new CancellationTokenSource(TimeSpan.FromMilliseconds(100));
        await AssertThrowsAsync<OperationCanceledException>(runner.RunAsync(arguments, cancellation.Token));
    }

    private static async Task TestProcessOutputAndArgumentBoundaries()
    {
        if (OperatingSystem.IsWindows())
        {
            Console.WriteLine("SKIP 异步输出与参数边界: Windows 使用单独的进程行为测试");
            return;
        }

        var runner = new WinRarProcessRunner("/bin/sh");
        const string rawPassword = "password must stay visible";
        var result = await runner.RunAsync(
            ["-c", "printf '%s' \"$1\"; printf '%s' stderr-marker >&2; exit 7", "sh", rawPassword],
            CancellationToken.None);

        AssertEqual(7, result.ExitCode);
        // GPT-5, 2026-08-06：进程输出是原始诊断记录，密码文本明确不得被替换为 ***。
        AssertEqual(rawPassword, result.StandardOutput);
        AssertEqual("stderr-marker", result.StandardError);
        Assert(result.CommandLine.Contains(rawPassword, StringComparison.Ordinal),
            "完整命令行必须保留原始密码参数，不得脱敏");
    }

    private static async Task TestCommandLogArguments()
    {
        var root = Path.Combine(Path.GetTempPath(), $"batch-compress-command-log-{Guid.NewGuid():N}");
        var source = Path.Combine(root, "source.txt");
        Directory.CreateDirectory(root);
        File.WriteAllText(source, "source");

        var snapshots = new List<OperationProgressInfo>();
        try
        {
            var engine = new CommandReportingArchiveEngine();
            await new BatchOperationService(engine, new TestSystemIntegration()).BatchCompressAsync(
                [source],
                new BatchOperationOptions
                {
                    OutputPath = Path.Combine(root, "out"),
                    Extension = "rar",
                    ExistingFileMode = ExistingFileMode.Overwrite
                },
                new SnapshotProgress(snapshots),
                CancellationToken.None);

            var commandMessage = snapshots
                .Select(snapshot => snapshot.Message)
                .FirstOrDefault(message => message.Contains("[压缩命令] command", StringComparison.Ordinal));
            Assert(commandMessage?.Contains("secret password", StringComparison.Ordinal) == true,
                "批处理命令日志必须原样包含引擎返回的完整参数");
        }
        finally
        {
            if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
        }
    }

    private static async Task TestCurrentSourcePathProgress()
    {
        var root = Path.Combine(Path.GetTempPath(), $"batch-compress-progress-path-{Guid.NewGuid():N}");
        var source = Path.Combine(root, "same-name.txt");
        Directory.CreateDirectory(root);
        File.WriteAllText(source, "source");

        try
        {
            var snapshots = new List<OperationProgressInfo>();
            await new BatchOperationService(new TestArchiveEngine(), new TestSystemIntegration()).BatchCompressAsync(
                [source],
                new BatchOperationOptions
                {
                    OutputPath = Path.Combine(root, "out"),
                    Extension = "7z",
                    ExistingFileMode = ExistingFileMode.Overwrite
                },
                new SnapshotProgress(snapshots),
                CancellationToken.None);

            Assert(snapshots.Any(snapshot => string.Equals(snapshot.CurrentSourcePath, source, StringComparison.Ordinal)),
                "进度快照必须保留完整来源路径，以便 GUI 定位并高亮当前行");
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    private static async Task TestBatchPreflightSafety()
    {
        var root = Path.Combine(Path.GetTempPath(), $"batch-compress-preflight-{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);
        try
        {
            var firstDirectory = Path.Combine(root, "first");
            var secondDirectory = Path.Combine(root, "second");
            Directory.CreateDirectory(firstDirectory);
            Directory.CreateDirectory(secondDirectory);
            var first = Path.Combine(firstDirectory, "report.txt");
            var second = Path.Combine(secondDirectory, "report.txt");
            File.WriteAllText(first, "first source");
            File.WriteAllText(second, "second source");
            var output = Path.Combine(root, "not-created");
            var engine = new RecordingArchiveEngine();
            var snapshots = new List<OperationProgressInfo>();

            await new BatchOperationService(engine, new TestSystemIntegration()).BatchCompressAsync(
                [first, second],
                new BatchOperationOptions
                {
                    OutputPath = output,
                    Extension = "7z",
                    ExistingFileMode = ExistingFileMode.Overwrite,
                    DeleteSourceAfter = true
                },
                new SnapshotProgress(snapshots),
                CancellationToken.None);

            AssertEqual(0, engine.CompressionCalls);
            Assert(!Directory.Exists(output), "预检失败不得创建输出目录");
            Assert(File.Exists(first) && File.Exists(second), "预检失败不得删除来源");
            AssertEqual(2, snapshots.Last().FailCount);
            Assert(snapshots.Last().Message.Contains("report.txt.7z", StringComparison.Ordinal),
                "冲突提示应包含目标归档路径");

            // 一个输出目标若等于本批次的另一个输入，也必须在覆盖该输入前阻止。
            var overlapDirectory = Path.Combine(root, "source-output-overlap");
            Directory.CreateDirectory(overlapDirectory);
            var source = Path.Combine(overlapDirectory, "notes.txt");
            var sourceArchive = Path.Combine(overlapDirectory, "notes.txt.7z");
            File.WriteAllText(source, "source");
            File.WriteAllText(sourceArchive, "another source");
            engine = new RecordingArchiveEngine();
            snapshots = [];
            await new BatchOperationService(engine, new TestSystemIntegration()).BatchCompressAsync(
                [source, sourceArchive],
                new BatchOperationOptions
                {
                    OutputPath = overlapDirectory,
                    Extension = "7z",
                    ExistingFileMode = ExistingFileMode.Overwrite,
                    DeleteSourceAfter = true
                },
                new SnapshotProgress(snapshots),
                CancellationToken.None);
            AssertEqual(0, engine.CompressionCalls);
            Assert(File.Exists(source) && File.Exists(sourceArchive), "输出覆盖来源的预检失败不得删除来源");

            // 输出目录嵌入一个明确选择的待压缩目录时，不得把归档写回来源树。
            var tree = Path.Combine(root, "tree");
            Directory.CreateDirectory(tree);
            File.WriteAllText(Path.Combine(tree, "inside.txt"), "inside");
            var nestedOutput = Path.Combine(tree, "out");
            engine = new RecordingArchiveEngine();
            snapshots = [];
            await new BatchOperationService(engine, new TestSystemIntegration()).BatchCompressAsync(
                [tree],
                new BatchOperationOptions
                {
                    OutputPath = nestedOutput,
                    Extension = "7z",
                    ExistingFileMode = ExistingFileMode.Overwrite
                },
                new SnapshotProgress(snapshots),
                CancellationToken.None);
            AssertEqual(0, engine.CompressionCalls);
            Assert(!Directory.Exists(nestedOutput), "重叠预检不得创建嵌套输出目录");

            // 目录批次的输出目录若是集合根的直接子项，只排除该目录；其它来源照常进入计划。
            var collection = Path.Combine(root, "collection");
            var collectionOutput = Path.Combine(collection, "out");
            Directory.CreateDirectory(collectionOutput);
            var collectionSource = Path.Combine(collection, "a.txt");
            File.WriteAllText(collectionSource, "a");
            var collected = new BatchOperationService(new TestArchiveEngine(), new TestSystemIntegration())
                .LoadCompressionSourcesFromFolder(collection, skipProcessed: false, collectionOutput);
            AssertEqual(1, collected.Count);
            AssertEqual(collectionSource, collected[0]);

            var collectionPlan = BatchPreflightService.InspectCompression(
                Directory.GetFileSystemEntries(collection),
                new BatchOperationOptions
                {
                    SourceCollectionRoot = collection,
                    OutputPath = collectionOutput,
                    Extension = "7z",
                    ExistingFileMode = ExistingFileMode.Overwrite
                });
            Assert(collectionPlan.CanExecute, "输出目录作为集合根直接子项时应从来源列表排除");
            AssertEqual(1, collectionPlan.SourcePaths.Count);

            if (!OperatingSystem.IsWindows())
            {
                var symlinkTarget = Path.Combine(root, "outside-output-target");
                var outputSymlink = Path.Combine(collection, "output-link");
                Directory.CreateDirectory(symlinkTarget);
                Directory.CreateSymbolicLink(outputSymlink, symlinkTarget);
                Assert(BatchPreflightService.ShouldExcludeCollectionEntry(
                        collection, outputSymlink, outputSymlink),
                    "集合根下指向外部的输出目录符号链接仍应作为输出项排除");
            }

            // 分卷名称也属于目标占用范围：pack.rar 与 pack.part1.rar 不能同时生成。
            var volumeSourceOne = Path.Combine(root, "pack");
            var volumeSourceTwo = Path.Combine(root, "pack.part1");
            File.WriteAllText(volumeSourceOne, "one");
            File.WriteAllText(volumeSourceTwo, "two");
            var volumePlan = BatchPreflightService.InspectCompression(
                [volumeSourceOne, volumeSourceTwo],
                new BatchOperationOptions
                {
                    OutputPath = Path.Combine(root, "volume-output"),
                    Extension = "rar",
                    VolumeSize = "10",
                    VolumeSizeUnit = "m",
                    ExistingFileMode = ExistingFileMode.Overwrite
                });
            Assert(!volumePlan.CanExecute, "分卷成员与另一目标重名时必须阻止整批");

            var caseDistinct = SourceCollector.CollectCompressionSources(
                [Path.Combine(root, "A.txt"), Path.Combine(root, "a.txt")]);
            AssertEqual(2, caseDistinct.Count);
        }
        finally
        {
            if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
        }
    }

    private static async Task TestConcurrentPreparationProgressSnapshots()
    {
        var root = Path.Combine(Path.GetTempPath(), $"batch-compress-prepare-progress-{Guid.NewGuid():N}");
        var missingOne = Path.Combine(root, "missing-one.txt");
        var missingTwo = Path.Combine(root, "missing-two.txt");
        var progress = new DeferredProgress();
        try
        {
            await new BatchOperationService(new TestArchiveEngine(), new TestSystemIntegration()).BatchCompressAsync(
                [missingOne, missingTwo],
                new BatchOperationOptions
                {
                    OutputPath = Path.Combine(root, "out"),
                    Extension = "7z",
                    ExistingFileMode = ExistingFileMode.Overwrite,
                    MaxConcurrency = 2
                },
                progress,
                CancellationToken.None);

            AssertEqual(2, progress.Reports.Count);
            Assert(!ReferenceEquals(progress.Reports[0], progress.Reports[1]),
                "并发预处理的异步进度回调必须收到独立快照");
            Assert(progress.Reports.Select(report => report.Message)
                    .All(message => message.Contains(missingOne, StringComparison.Ordinal) ||
                                    message.Contains(missingTwo, StringComparison.Ordinal)),
                "排队后的进度消息仍应保留各自来源路径");
            Assert(progress.Reports.Select(report => report.Message).Distinct(StringComparer.Ordinal).Count() == 2,
                "两个延迟进度回调不得都变成最后一个来源的消息");
            Assert(progress.Reports.Select(report => report.NonExistCount).Order().SequenceEqual([1, 2]),
                "每条进度快照应保留报告时的缺失计数");
        }
        finally
        {
            if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
        }
    }

    // 消息文本、七类计数、关机时机与文件系统副作用，重构期间任何行为漂移都会在此暴露。
    private static async Task TestCompressionLoopCharacterization()
    {
        var L = LocalizationService.Instance.Strings;
        var root = Path.Combine(Path.GetTempPath(), $"batch-compress-char-c-{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);
        try
        {
            // 场景 1：不可创建格式计 1 条失败、返回格式消息、不进循环、也不触发关机。
            var engine = new ScriptedArchiveEngine();
            var system = new RecordingSystemIntegration();
            var snapshots = new List<OperationProgressInfo>();
            var service = new BatchOperationService(engine, system);
            await service.BatchCompressAsync(
                [root],
                new BatchOperationOptions { OutputPath = root, Extension = "iso", ShutdownAfterComplete = true },
                new SnapshotProgress(snapshots), CancellationToken.None);
            var final = snapshots.Last();
            AssertEqual(1, final.FailCount);
            AssertEqual(0, final.SuccessCount);
            AssertEqual(0, engine.CompressionInputs.Count);
            AssertEqual(0, system.ShutdownCalls);
            Assert(final.Message == string.Format(L.FormatCannotCreate, "iso", ArchiveFormatCatalog.CreateFormatsText),
                "格式拒绝消息必须与词条完全一致");

            // 场景 2：源不存在计 NotFound、消息含原始路径、引擎零调用。
            var missing = Path.Combine(root, "missing-item");
            snapshots.Clear();
            await service.BatchCompressAsync(
                [missing],
                new BatchOperationOptions { OutputPath = root, Extension = "7z", ExistingFileMode = ExistingFileMode.Overwrite },
                new SnapshotProgress(snapshots), CancellationToken.None);
            final = snapshots.Last();
            AssertEqual(1, final.NonExistCount);
            AssertEqual(0, engine.CompressionInputs.Count);
            Assert(final.Message == string.Format(L.ItemNotFound, missing), "源不存在消息必须与词条一致");

            // 场景 3：SkipAlreadyProcessed 对名称含【已压缩】的源静默跳过（计数增加但不产生任何进度报告）。
            var doneSource = Path.Combine(root, "proj【已压缩】");
            Directory.CreateDirectory(doneSource);
            snapshots.Clear();
            await service.BatchCompressAsync(
                [doneSource],
                new BatchOperationOptions
                {
                    OutputPath = root, Extension = "7z", SkipAlreadyProcessed = true,
                    ExistingFileMode = ExistingFileMode.Overwrite
                },
                new SnapshotProgress(snapshots), CancellationToken.None);
            Assert(snapshots.Count == 0, "跳过已处理项不得产生进度报告");
            AssertEqual(0, engine.CompressionInputs.Count);
            Directory.Delete(doneSource);

            // 场景 4：Overwrite 删除已有输出，包括新式分卷与旧式 .r00 遗留（rar 双组枚举）。
            var overwriteSource = Path.Combine(root, "ow-src");
            var overwriteOutput = Path.Combine(root, "ow-out");
            Directory.CreateDirectory(overwriteSource);
            Directory.CreateDirectory(overwriteOutput);
            File.WriteAllText(Path.Combine(overwriteSource, "a.txt"), "a");
            File.WriteAllText(Path.Combine(overwriteOutput, "ow-src.rar"), "old");
            File.WriteAllText(Path.Combine(overwriteOutput, "ow-src.part1.rar"), "old");
            File.WriteAllText(Path.Combine(overwriteOutput, "ow-src.r00"), "old");
            await service.BatchCompressAsync(
                [overwriteSource],
                new BatchOperationOptions { OutputPath = overwriteOutput, Extension = "rar", ExistingFileMode = ExistingFileMode.Overwrite },
                new SnapshotProgress(snapshots), CancellationToken.None);
            AssertEqual(1, engine.CompressionInputs.Count);
            AssertEqual(1, snapshots.Last().SuccessCount);
            Assert(!File.Exists(Path.Combine(overwriteOutput, "ow-src.rar")), "覆盖模式必须删除旧主卷");
            Assert(!File.Exists(Path.Combine(overwriteOutput, "ow-src.part1.rar")), "覆盖模式必须删除旧 part 分卷");
            Assert(!File.Exists(Path.Combine(overwriteOutput, "ow-src.r00")), "覆盖模式必须删除旧式 .r00 遗留分卷");

            // 场景 5：DeleteSourceAfter 成功时源被删、成功消息为 ItemSucceeded、无后处理失败。
            snapshots.Clear();
            var deleteSource = Path.Combine(root, "del-src");
            Directory.CreateDirectory(deleteSource);
            File.WriteAllText(Path.Combine(deleteSource, "a.txt"), "a");
            await service.BatchCompressAsync(
                [deleteSource],
                new BatchOperationOptions
                {
                    OutputPath = Path.Combine(root, "del-out"), Extension = "7z",
                    ExistingFileMode = ExistingFileMode.Overwrite, DeleteSourceAfter = true
                },
                new SnapshotProgress(snapshots), CancellationToken.None);
            final = snapshots.Last();
            AssertEqual(1, final.SuccessCount);
            AssertEqual(0, final.PostProcessFailCount);
            Assert(!Directory.Exists(deleteSource), "删除源后处理必须移除源目录");
            Assert(final.Message == string.Format(L.ItemSucceeded, "del-src"), "纯成功消息必须与词条一致");

            // 场景 6：MoveSourceAfter 成功时源移入【已压缩】目录。
            var moveSource = Path.Combine(root, "mv-src");
            Directory.CreateDirectory(moveSource);
            File.WriteAllText(Path.Combine(moveSource, "a.txt"), "a");
            await service.BatchCompressAsync(
                [moveSource],
                new BatchOperationOptions
                {
                    OutputPath = Path.Combine(root, "mv-out"), Extension = "7z",
                    ExistingFileMode = ExistingFileMode.Overwrite, MoveSourceAfter = true
                },
                new SnapshotProgress(snapshots), CancellationToken.None);
            Assert(!Directory.Exists(moveSource), "移动后源不得留在原位置");
            Assert(Directory.Exists(Path.Combine(root, "【已压缩】", "mv-src")), "源必须移入【已压缩】目录");

            // 场景 7：引擎失败计 FailCount 并输出 ItemFailed 消息（含名称与引擎错误）。
            var failingEngine = new ScriptedArchiveEngine(input => true);
            var failSource = Path.Combine(root, "fail-src");
            Directory.CreateDirectory(failSource);
            snapshots.Clear();
            await new BatchOperationService(failingEngine, system).BatchCompressAsync(
                [failSource],
                new BatchOperationOptions { OutputPath = root, Extension = "7z", ExistingFileMode = ExistingFileMode.Overwrite },
                new SnapshotProgress(snapshots), CancellationToken.None);
            final = snapshots.Last();
            AssertEqual(1, final.FailCount);
            Assert(final.Message == string.Format(L.ItemFailed, "fail-src", "scripted failure"),
                "失败消息必须与词条一致并包含引擎错误");

            // 场景 8：MaxSizeGB 达到后停止后续任务（真实 1MB 输出超过 0.0005GB 上限）。
            var sizeEngine = new ScriptedArchiveEngine { WriteRealOutput = true };
            var firstSource = Path.Combine(root, "size-a");
            var secondSource = Path.Combine(root, "size-b");
            Directory.CreateDirectory(firstSource);
            Directory.CreateDirectory(secondSource);
            snapshots.Clear();
            await new BatchOperationService(sizeEngine, system).BatchCompressAsync(
                [firstSource, secondSource],
                new BatchOperationOptions
                {
                    OutputPath = Path.Combine(root, "size-out"), Extension = "7z",
                    ExistingFileMode = ExistingFileMode.Overwrite, MaxSizeGB = 0.0005
                },
                new SnapshotProgress(snapshots), CancellationToken.None);
            AssertEqual(1, sizeEngine.CompressionInputs.Count);
            AssertEqual(1, snapshots.Last().SuccessCount);
            Assert(snapshots.Any(info => info.Message == L.SizeLimitReached), "达到大小上限必须报告 SizeLimitReached");

            // 场景 9：全部任务结束后才请求关机（含跳过项的批次同样触发）。
            var shutdownEngine = new ScriptedArchiveEngine();
            var shutdownSource = Path.Combine(root, "sd-src");
            Directory.CreateDirectory(shutdownSource);
            var shutdownSystem = new RecordingSystemIntegration();
            await new BatchOperationService(shutdownEngine, shutdownSystem).BatchCompressAsync(
                [shutdownSource],
                new BatchOperationOptions
                {
                    OutputPath = root, Extension = "7z", ExistingFileMode = ExistingFileMode.Overwrite,
                    ShutdownAfterComplete = true
                },
                new SnapshotProgress(snapshots), CancellationToken.None);
            AssertEqual(1, shutdownSystem.ShutdownCalls);
        }
        finally
        {
            Directory.Delete(root, true);
        }
    }

    // GLM-5.3, 2026-11-03：批次 2 表征测试——解压循环。
    private static async Task TestDecompressionLoopCharacterization()
    {
        var L = LocalizationService.Instance.Strings;
        var root = Path.Combine(Path.GetTempPath(), $"batch-compress-char-d-{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);
        try
        {
            var engine = new ScriptedArchiveEngine();
            var system = new RecordingSystemIntegration();
            var service = new BatchOperationService(engine, system);
            var snapshots = new List<OperationProgressInfo>();

            // 场景 1：分卷编号重复（.1 与 .001 并存）计歧义并跳过、引擎零调用。
            File.WriteAllText(Path.Combine(root, "dup.7z.1"), "1");
            File.WriteAllText(Path.Combine(root, "dup.7z.001"), "001");
            await service.BatchDecompressAsync(
                [new FileEntry { FilePath = Path.Combine(root, "dup.7z.001"), FileSize = 3 }],
                new BatchOperationOptions { OutputPath = Path.Combine(root, "o1"), Extension = "7z", ExistingFileMode = ExistingFileMode.Overwrite },
                new SnapshotProgress(snapshots), CancellationToken.None);
            var final = snapshots.Last();
            AssertEqual(1, final.AmbiguousArchiveCount);
            AssertEqual(1, final.IgnoreCount);
            AssertEqual(0, engine.ExtractionInputs.Count);
            Assert(final.Message == string.Format(L.DuplicateVolumeSkipped, Path.Combine(root, "dup.7z.001")),
                "重复编号消息必须与词条一致");

            // 场景 2：同一分卷组重复出现时第二个任务静默去重。
            File.WriteAllText(Path.Combine(root, "same.7z.001"), "a");
            File.WriteAllText(Path.Combine(root, "same.7z.002"), "b");
            var sameEntry = new FileEntry { FilePath = Path.Combine(root, "same.7z.001"), FileSize = 1 };
            snapshots.Clear();
            await service.BatchDecompressAsync(
                [sameEntry, sameEntry],
                new BatchOperationOptions { OutputPath = Path.Combine(root, "o2"), Extension = "7z", ExistingFileMode = ExistingFileMode.Overwrite },
                new SnapshotProgress(snapshots), CancellationToken.None);
            final = snapshots.Last();
            AssertEqual(1, engine.ExtractionInputs.Count);
            AssertEqual(1, final.IgnoreCount);

            // 场景 3：SkipAlreadyProcessed 对名称含【已解压】的归档静默跳过。
            var processedArchive = Path.Combine(root, "done【已解压】.7z");
            File.WriteAllText(processedArchive, "x");
            snapshots.Clear();
            await service.BatchDecompressAsync(
                [new FileEntry { FilePath = processedArchive, FileSize = 1 }],
                new BatchOperationOptions
                {
                    OutputPath = Path.Combine(root, "o3"), Extension = "7z", SkipAlreadyProcessed = true,
                    ExistingFileMode = ExistingFileMode.Overwrite
                },
                new SnapshotProgress(snapshots), CancellationToken.None);
            Assert(snapshots.Count == 0, "跳过已处理归档不得产生进度报告");
            AssertEqual(1, engine.ExtractionInputs.Count);

            // 场景 4：TXT 条目密码优先于全局 CustomPassword，条目为空时才回退。
            var pwArchive = Path.Combine(root, "pw.7z");
            File.WriteAllText(pwArchive, "x");
            await service.BatchDecompressAsync(
                [
                    new FileEntry { FilePath = pwArchive, Password = "txt-pw", FileSize = 1 },
                    new FileEntry { FilePath = processedArchive, Password = null, FileSize = 1 }
                ],
                new BatchOperationOptions
                {
                    OutputPath = Path.Combine(root, "o4"), Extension = "7z",
                    CustomPassword = "global-pw", ExistingFileMode = ExistingFileMode.Overwrite
                },
                new SnapshotProgress(snapshots), CancellationToken.None);
            AssertEqual("txt-pw", engine.ExtractionOptions[1].Password);
            AssertEqual("global-pw", engine.ExtractionOptions[2].Password);

            // 场景 5：数字分卷传递归一化格式（.001 不当作格式）。
            AssertEqual("7z", engine.ExtractionOptions[1].ArchiveFormat);

            // 场景 6：DeleteSourceAfter 删除整组分卷。
            var group = new[] { "grp.7z.001", "grp.7z.002", "grp.7z.003" };
            foreach (var name in group)
            {
                File.WriteAllText(Path.Combine(root, name), name);
            }

            await service.BatchDecompressAsync(
                [new FileEntry { FilePath = Path.Combine(root, group[0]), FileSize = 3 }],
                new BatchOperationOptions
                {
                    OutputPath = Path.Combine(root, "o6"), Extension = "7z",
                    ExistingFileMode = ExistingFileMode.Overwrite, DeleteSourceAfter = true
                },
                new SnapshotProgress(snapshots), CancellationToken.None);
            foreach (var name in group)
            {
                Assert(!File.Exists(Path.Combine(root, name)), $"删除源必须移除整组成员：{name}");
            }

            // 场景 7：MaxSizeGB 达到后停止后续任务（第一组真实 3MB 超过 0.002GB）。
            var sizeRoot = Path.Combine(root, "sizes");
            Directory.CreateDirectory(sizeRoot);
            foreach (var name in new[] { "s1.7z.001", "s1.7z.002", "s1.7z.003" })
            {
                File.WriteAllBytes(Path.Combine(sizeRoot, name), new byte[1024 * 1024]);
            }

            File.WriteAllBytes(Path.Combine(sizeRoot, "s2.7z"), new byte[1024 * 1024]);
            var sizeEngine = new ScriptedArchiveEngine();
            snapshots.Clear();
            await new BatchOperationService(sizeEngine, system).BatchDecompressAsync(
                [
                    new FileEntry { FilePath = Path.Combine(sizeRoot, "s1.7z.001"), FileSize = 3 },
                    new FileEntry { FilePath = Path.Combine(sizeRoot, "s2.7z"), FileSize = 1 }
                ],
                new BatchOperationOptions
                {
                    OutputPath = Path.Combine(root, "o7"), Extension = "7z",
                    ExistingFileMode = ExistingFileMode.Overwrite, MaxSizeGB = 0.002
                },
                new SnapshotProgress(snapshots), CancellationToken.None);
            AssertEqual(1, sizeEngine.ExtractionInputs.Count);
            Assert(snapshots.Any(info => info.Message == L.SizeLimitReached), "解压达到大小上限必须报告");

            // 场景 8：全部解压任务结束后请求一次关机。
            var shutdownEngine = new ScriptedArchiveEngine();
            var shutdownSystem = new RecordingSystemIntegration();
            var shutdownArchive = Path.Combine(root, "sd.7z");
            File.WriteAllText(shutdownArchive, "x");
            await new BatchOperationService(shutdownEngine, shutdownSystem).BatchDecompressAsync(
                [new FileEntry { FilePath = shutdownArchive, FileSize = 1 }],
                new BatchOperationOptions
                {
                    OutputPath = Path.Combine(root, "o8"), Extension = "7z",
                    ExistingFileMode = ExistingFileMode.Overwrite, ShutdownAfterComplete = true
                },
                new SnapshotProgress(snapshots), CancellationToken.None);
            AssertEqual(1, shutdownSystem.ShutdownCalls);
        }
        finally
        {
            Directory.Delete(root, true);
        }
    }
}
