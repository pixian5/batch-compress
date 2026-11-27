using System.Collections.Generic;
using BatchCompress.Avalonia.Core.Interfaces;
using BatchCompress.Avalonia.Core.Models;
using BatchCompress.Avalonia.Core.Services;
using BatchCompress.Avalonia.Localization;

// Gemini 4 Hydrogen, 2026-11-27：批次 46——从 Program.PipelineTests.cs 拆出的来源收集、路径规范化、分卷校验与后处理冲突测试。
internal static partial class Program
{
    // GLM-5.3, 2026-11-03：批次 2 表征测试——来源目录扫描。
    private static Task TestSourceCollectionCharacterization()
    {
        var root = Path.Combine(Path.GetTempPath(), $"batch-compress-char-s-{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);
        try
        {
            foreach (var name in new[] { "keep", "done【已压缩】", "done【已解压】", ".tmp-partial" })
            {
                Directory.CreateDirectory(Path.Combine(root, name));
            }

            File.WriteAllText(Path.Combine(root, "desktop.ini"), "meta");
            File.WriteAllText(Path.Combine(root, ".DS_Store"), "meta");

            var service = new BatchOperationService(new ScriptedArchiveEngine(), new RecordingSystemIntegration());
            var skipped = service.LoadCompressionSourcesFromFolder(root, skipProcessed: true)
                .Select(Path.GetFileName)
                .ToArray();
            AssertEqual(1, skipped.Length);
            AssertEqual("keep", skipped[0]);

            var all = service.LoadCompressionSourcesFromFolder(root, skipProcessed: false)
                .Select(Path.GetFileName)
                .OrderBy(name => name, StringComparer.Ordinal)
                .ToArray();
            AssertEqual(3, all.Length);
            Assert(!all.Contains("desktop.ini"), "系统元数据必须始终被过滤");
            Assert(!all.Contains(".DS_Store"), "macOS 元数据必须始终被过滤");
            Assert(!all.Contains(".tmp-partial"), ".tmp 前缀必须始终被过滤");
        }
        finally
        {
            Directory.Delete(root, true);
        }

        return Task.CompletedTask;
    }

    // GLM-5.3, 2026-11-03：大小写歧义只在区分大小写的文件系统（Linux CI）可构造，
    // 其余平台未挂载大小写敏感卷时输出 SKIP；若设置 BATCHCOMPRESS_CASE_SENSITIVE_DIR 则直接实测。
    private static async Task TestCaseAmbiguitySkip()
    {
        var baseDir = ResolveCaseSensitiveBaseDirectory();
        if (baseDir == null)
        {
            Console.WriteLine("SKIP 大小写歧义跳过: 需要区分大小写的文件系统（可设置 BATCHCOMPRESS_CASE_SENSITIVE_DIR）");
            return;
        }

        var L = LocalizationService.Instance.Strings;
        var root = Path.Combine(baseDir, $"batch-compress-char-a-{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);
        try
        {
            File.WriteAllText(Path.Combine(root, "v.7z"), "one");
            File.WriteAllText(Path.Combine(root, "V.7z"), "two");
            var engine = new ScriptedArchiveEngine();
            var snapshots = new List<OperationProgressInfo>();
            // 请求不存在的字面变体触发忽略大小写枚举，命中两个条目即歧义。
            var requested = Path.Combine(root, "v.7Z");
            await new BatchOperationService(engine, new RecordingSystemIntegration()).BatchDecompressAsync(
                [new FileEntry { FilePath = requested, FileSize = 1 }],
                new BatchOperationOptions { OutputPath = Path.Combine(root, "out"), Extension = "7z", ExistingFileMode = ExistingFileMode.Overwrite },
                new SnapshotProgress(snapshots), CancellationToken.None);
            var final = snapshots.Last();
            AssertEqual(1, final.AmbiguousArchiveCount);
            AssertEqual(1, final.IgnoreCount);
            AssertEqual(0, engine.ExtractionInputs.Count);
            Assert(final.Message == string.Format(L.CaseAmbiguitySkipped, requested), "大小写歧义消息必须与词条一致");
        }
        finally
        {
            Directory.Delete(root, true);
        }
    }

    // GLM-5.3, 2026-11-04：批次 2 阶段 2——尾部斜杠缺陷阻断的规范化基线。
    // 锁定：SourceCollector.NormalizePath 消除尾部目录分隔符（文件名不得为空串）、
    // 根路径保持原样、相对路径转绝对、空输入与非法路径防御。
    private static Task TestPathNormalization()
    {
        var root = Path.Combine(Path.GetTempPath(), $"batch-compress-norm-{Guid.NewGuid():N}");
        var directory = Path.Combine(root, "dir");
        Directory.CreateDirectory(directory);
        try
        {
            // 尾部主分隔符消除：GetFileName("/x/dir/") 历史上返回空串导致生成 ".7z" 空名归档。
            var withSlash = SourceCollector.NormalizePath(directory + Path.DirectorySeparatorChar);
            AssertEqual(directory, withSlash);
            AssertEqual("dir", Path.GetFileName(withSlash));

            // 备用分隔符（Windows 的 “/”）与连续多个尾部斜杠同样消除。
            AssertEqual(directory, SourceCollector.NormalizePath(
                directory + Path.AltDirectorySeparatorChar));
            AssertEqual(directory, SourceCollector.NormalizePath(
                directory + new string(Path.DirectorySeparatorChar, 3)));

            // 文件系统根不得截断：TrimEnd("/") 会得到空串，Windows 上 “C:\”→“C:” 会变成盘符相对路径。
            var volumeRoot = Path.GetPathRoot(Path.GetFullPath(root)) ?? Path.DirectorySeparatorChar.ToString();
            AssertEqual(volumeRoot, SourceCollector.NormalizePath(volumeRoot));

            // 相对路径转绝对；首尾空格剔除。
            AssertEqual(
                Path.Combine(Directory.GetCurrentDirectory(), "dir"),
                SourceCollector.NormalizePath("  dir  "));

            // 空与空白输入返回空串；非法路径不抛异常、原样返回（交由服务层按 ItemNotFound 报告）。
            AssertEqual(string.Empty, SourceCollector.NormalizePath("   "));
            AssertEqual(string.Empty, SourceCollector.NormalizePath(null));
            var invalid = "bad\0path";
            AssertEqual(invalid, SourceCollector.NormalizePath(invalid));
        }
        finally
        {
            Directory.Delete(root, true);
        }

        return Task.CompletedTask;
    }

    // GLM-5.3, 2026-11-04：批次 2 阶段 2——统一来源收集行为基线。
    // 锁定：GUI/CLI 共用收集器的空行与元数据过滤、保序去重、尾部斜杠识别、
    // 密码本交替行配对、解压格式过滤与 CLI 目录展开/首卷折叠/种子优先去重。
    private static Task TestUnifiedSourceCollection()
    {
        var L = LocalizationService.Instance.Strings;
        var root = Path.Combine(Path.GetTempPath(), $"batch-compress-collect-{Guid.NewGuid():N}");
        var project = Path.Combine(root, "proj");
        var archives = Path.Combine(root, "archives");
        Directory.CreateDirectory(project);
        Directory.CreateDirectory(archives);
        File.WriteAllText(Path.Combine(root, "a.7z"), "a");
        File.WriteAllText(Path.Combine(root, "b.rar"), "b");
        File.WriteAllText(Path.Combine(root, "note.txt"), "n");
        File.WriteAllText(Path.Combine(root, ".DS_Store"), "meta");
        File.WriteAllText(Path.Combine(archives, "g.7z.001"), "1");
        File.WriteAllText(Path.Combine(archives, "g.7z.002"), "2");
        File.WriteAllText(Path.Combine(archives, "other.rar"), "rar");
        try
        {
            // 1. 压缩来源（GUI 多行文本与 CLI 路径集合共用口径）：
            //    空行/空白行/元数据行剔除；目录尾斜杠规范化后文件名为 “proj” 而非空串；
            //    等价写法（尾斜杠、/./ 段）保序去重为一条。
            var projectWithSlash = project + Path.DirectorySeparatorChar;
            var multiLineText = string.Join("\r\n",
            [
                "",
                "   ",
                "  " + projectWithSlash,
                Path.Combine(root, "a.7z"),
                Path.Combine(root, ".DS_Store"),
                projectWithSlash,
                Path.Combine(root, ".", "proj")
            ]);
            var sources = SourceCollector.CollectCompressionSourcesFromText(multiLineText);
            AssertEqual(2, sources.Count);
            AssertEqual(project, sources[0]);
            AssertEqual("proj", Path.GetFileName(sources[0]));
            AssertEqual(Path.Combine(root, "a.7z"), sources[1]);

            // CLI 入口等价：同一批原始路径得到同一结果。
            var cliSources = SourceCollector.CollectCompressionSources(
            [
                projectWithSlash,
                Path.Combine(root, "a.7z"),
                projectWithSlash
            ]);
            AssertEqual(sources.Count, cliSources.Count);
            AssertEqual(sources[0], cliSources[0]);
            AssertEqual(sources[1], cliSources[1]);

            // 2. 密码本交替行：缺失文件连同密码整对跳过；文件行尾斜杠仍可识别；密码配对不漂移。
            var missing = Path.Combine(root, "missing.7z");
            var bookText = string.Join("\n",
            [
                Path.Combine(root, "a.7z"), "pw-a",
                missing, "pw-missing",
                Path.Combine(root, "b.rar") + Path.DirectorySeparatorChar, "pw-b"
            ]);
            var book = SourceCollector.CollectPasswordBookEntries(bookText);
            AssertEqual(2, book.Count);
            AssertEqual(Path.Combine(root, "a.7z"), book[0].FilePath);
            AssertEqual("pw-a", book[0].Password);
            AssertEqual(Path.Combine(root, "b.rar"), book[1].FilePath);
            AssertEqual("pw-b", book[1].Password);

            // 3. 解压文件行：存在性 + 格式过滤（txt 与 rar 不入 7z 任务）、路径规范化。
            var files = SourceCollector.CollectDecompressionFilesFromText(
                string.Join("\n", [Path.Combine(root, "b.rar"), "  " + Path.Combine(root, "a.7z") + "  ", Path.Combine(root, "note.txt")]),
                "7z");
            AssertEqual(1, files.Count);
            AssertEqual(Path.Combine(root, "a.7z"), files[0].FilePath);
            AssertEqual(1L, files[0].FileSize);

            // 4. CLI 解压收集：目录展开按目标格式过滤、--input 与目录展开折叠到同一首卷后去重、
            //    密码本种子条目优先保留（含密码）。
            var service = new BatchOperationService(new ScriptedArchiveEngine(), new RecordingSystemIntegration());
            var seed = new List<FileEntry>
            {
                new() { FilePath = Path.Combine(archives, "g.7z.001"), Password = "seed-pw", FileSize = 1 }
            };
            var entries = SourceCollector.CollectDecompressionEntries(
                seed,
                [archives, Path.Combine(archives, "g.7z.001"), missing],
                "7z",
                skipProcessed: false,
                batchService: service);
            AssertEqual(1, entries.Count);
            AssertEqual(Path.Combine(archives, "g.7z.001"), entries[0].FilePath);
            AssertEqual("seed-pw", entries[0].Password);

            // 尾部斜杠目录（GUI 风格输入走 CLI 收集口径）同样完成展开与去重。
            var slashedEntries = SourceCollector.CollectDecompressionEntries(
                null,
                [archives + Path.DirectorySeparatorChar],
                "7z",
                skipProcessed: false,
                batchService: service);
            AssertEqual(1, slashedEntries.Count);
            AssertEqual(Path.Combine(archives, "g.7z.001"), slashedEntries[0].FilePath);

            // 端到端守卫：带尾斜杠的来源经收集后进入批处理循环，消息必须携带真实目录名。
            var snapshots = new List<OperationProgressInfo>();
            new BatchOperationService(new ScriptedArchiveEngine(), new RecordingSystemIntegration())
                .BatchCompressAsync(
                    SourceCollector.CollectCompressionSources([projectWithSlash]),
                    new BatchOperationOptions
                    {
                        OutputPath = Path.Combine(root, "out"),
                        Extension = "7z",
                        ExistingFileMode = ExistingFileMode.Overwrite
                    },
                    new SnapshotProgress(snapshots),
                    CancellationToken.None)
                .GetAwaiter().GetResult();
            Assert(snapshots.Count > 0 && snapshots.Last().CurrentFile == "proj",
                "尾斜杠来源必须以目录名进入压缩任务，禁止空名归档");
            Assert(snapshots.Last().Message == string.Format(L.ItemSucceeded, "proj"),
                "成功消息必须携带真实目录名，禁止空名归档任务");
        }
        finally
        {
            Directory.Delete(root, true);
        }

        return Task.CompletedTask;
    }

    // GLM-5.3, 2026-11-04：外部审核补强——取消传播表征。锁定：
    // 循环头预取消与引擎执行中取消均抛 OperationCanceledException、绝不触发关机、
    // 附件暂存目录在 finally 中可靠清理。
    private static async Task TestCancellationPropagation()
    {
        var root = Path.Combine(Path.GetTempPath(), $"batch-compress-char-cancel-{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);
        var source = Path.Combine(root, "src");
        Directory.CreateDirectory(source);
        File.WriteAllText(Path.Combine(source, "a.txt"), "a");
        try
        {
            // 场景 1：进入循环前已取消——循环头即抛、引擎零调用、不关机。
            var engine = new ScriptedArchiveEngine { ThrowCancellationOnCompress = true };
            var system = new RecordingSystemIntegration();
            using var cancelled = new CancellationTokenSource();
            cancelled.Cancel();
            await AssertThrowsAsync<OperationCanceledException>(
                new BatchOperationService(engine, system).BatchCompressAsync(
                    [source],
                    new BatchOperationOptions
                    {
                        OutputPath = Path.Combine(root, "out"), Extension = "7z",
                        ExistingFileMode = ExistingFileMode.Overwrite, ShutdownAfterComplete = true
                    },
                    new SnapshotProgress([]), cancelled.Token));
            AssertEqual(0, engine.CompressionInputs.Count);
            AssertEqual(0, system.ShutdownCalls);

            // 场景 2：引擎执行中取消——附件暂存目录必须被 finally 清理、不关机。
            var delayingEngine = new ScriptedArchiveEngine { CancelAfterMilliseconds = 400 };
            using var cts = new CancellationTokenSource();
            cts.CancelAfter(50);
            await AssertThrowsAsync<OperationCanceledException>(
                new BatchOperationService(delayingEngine, system).BatchCompressAsync(
                    [source],
                    new BatchOperationOptions
                    {
                        OutputPath = Path.Combine(root, "out2"), Extension = "7z",
                        ExistingFileMode = ExistingFileMode.Overwrite, ShutdownAfterComplete = true,
                        AddEnclosures = true,
                        EnclosureDirectories = [Path.Combine(root, "definitely-not-exists-attachment")]
                    },
                    new SnapshotProgress([]), cts.Token));
            AssertEqual(1, delayingEngine.CompressionInputs.Count);
            AssertEqual(0, system.ShutdownCalls);
            Assert(Directory.GetDirectories(Path.GetTempPath(), "batch-compress-attachments-*").Length == 0,
                "取消后附件暂存目录必须被 finally 可靠清理，不得残留");
        }
        finally
        {
            Directory.Delete(root, true);
        }
    }

    // GLM-5.3, 2026-11-04：外部审核补强——Update 模式在服务层原样透传：
    // 不预删除已有输出、不跳过任务，ExistingFileMode 原样传给引擎（差异化由命令构建层实现）。
    private static async Task TestUpdateModePassthrough()
    {
        var root = Path.Combine(Path.GetTempPath(), $"batch-compress-char-update-{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);
        try
        {
            // 压缩侧：已有输出存在时 Update 仍调用引擎且不删除旧输出。
            var source = Path.Combine(root, "src");
            var output = Path.Combine(root, "out");
            Directory.CreateDirectory(source);
            Directory.CreateDirectory(output);
            File.WriteAllText(Path.Combine(source, "a.txt"), "a");
            var existing = Path.Combine(output, "src.7z");
            File.WriteAllText(existing, "old output");
            var engine = new ScriptedArchiveEngine();
            await new BatchOperationService(engine, new RecordingSystemIntegration()).BatchCompressAsync(
                [source],
                new BatchOperationOptions { OutputPath = output, Extension = "7z", ExistingFileMode = ExistingFileMode.Update },
                new SnapshotProgress([]), CancellationToken.None);
            AssertEqual(1, engine.CompressionInputs.Count);
            Assert(File.Exists(existing), "Update 模式不得预删除已有输出");
            AssertEqual(ExistingFileMode.Update, engine.CompressionOptions[0].ExistingFileMode);

            // 解压侧：ExistingFileMode 原样透传给引擎选项。
            var archive = Path.Combine(root, "book.7z");
            File.WriteAllText(archive, "archive");
            await new BatchOperationService(engine, new RecordingSystemIntegration()).BatchDecompressAsync(
                [new FileEntry { FilePath = archive, FileSize = 7 }],
                new BatchOperationOptions
                {
                    OutputPath = Path.Combine(root, "ex"), Extension = "7z", ExistingFileMode = ExistingFileMode.Update
                },
                new SnapshotProgress([]), CancellationToken.None);
            AssertEqual(ExistingFileMode.Update, engine.ExtractionOptions[0].ExistingFileMode);
        }
        finally
        {
            Directory.Delete(root, true);
        }
    }

    // GLM-5.3, 2026-11-04：外部审核补强——CurrentFile 刷新基线。
    // 锁定：解压预检失败（缺首卷等）与压缩源不存在分支均在刷新 CurrentFile 之前 continue，
    // 最后一次进度报告的 CurrentFile 保持上一个成功项，不显示失败项名。
    private static async Task TestCurrentFileBaseline()
    {
        var root = Path.Combine(Path.GetTempPath(), $"batch-compress-char-cf-{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);
        try
        {
            // 解压侧：先成功一项，再遇到缺首卷组——CurrentFile 不得刷新为失败项。
            File.WriteAllText(Path.Combine(root, "ok.7z"), "x");
            File.WriteAllText(Path.Combine(root, "broken.7z.002"), "x");
            File.WriteAllText(Path.Combine(root, "broken.7z.003"), "x");
            var engine = new ScriptedArchiveEngine();
            var snapshots = new List<OperationProgressInfo>();
            await new BatchOperationService(engine, new RecordingSystemIntegration()).BatchDecompressAsync(
                [
                    new FileEntry { FilePath = Path.Combine(root, "ok.7z"), FileSize = 1 },
                    new FileEntry { FilePath = Path.Combine(root, "broken.7z.003"), FileSize = 1 }
                ],
                new BatchOperationOptions
                {
                    OutputPath = Path.Combine(root, "ex"), Extension = "7z", ExistingFileMode = ExistingFileMode.Overwrite
                },
                new SnapshotProgress(snapshots), CancellationToken.None);
            var final = snapshots.Last();
            AssertEqual(1, final.IncompleteVolumeCount);
            AssertEqual("ok.7z", final.CurrentFile);

            // 压缩侧：源不存在分支在 CurrentFile 赋值前 continue，保持上一项。
            snapshots.Clear();
            var good = Path.Combine(root, "good");
            Directory.CreateDirectory(good);
            await new BatchOperationService(new ScriptedArchiveEngine(), new RecordingSystemIntegration()).BatchCompressAsync(
                [good, Path.Combine(root, "gone")],
                new BatchOperationOptions
                {
                    OutputPath = Path.Combine(root, "co"), Extension = "7z", ExistingFileMode = ExistingFileMode.Overwrite
                },
                new SnapshotProgress(snapshots), CancellationToken.None);
            AssertEqual("good", snapshots.Last().CurrentFile);
            AssertEqual(1, snapshots.Last().NonExistCount);
        }
        finally
        {
            Directory.Delete(root, true);
        }
    }

    private static async Task TestVolumeValidationAndProgress()
    {
        var root = Path.Combine(Path.GetTempPath(), $"batch-compress-volume-progress-{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);
        try
        {
            var missingSecond = Path.Combine(root, "broken.7z.001");
            var third = Path.Combine(root, "broken.7z.003");
            File.WriteAllText(missingSecond, "1");
            File.WriteAllText(third, "3");
            var engine = new RecordingArchiveEngine();
            var snapshots = new List<OperationProgressInfo>();
            await new BatchOperationService(engine, new TestSystemIntegration()).BatchDecompressAsync(
                [new FileEntry { FilePath = missingSecond, FileSize = 1 }],
                new BatchOperationOptions { OutputPath = Path.Combine(root, "out"), Extension = "7z" },
                new SnapshotProgress(snapshots),
                CancellationToken.None);

            var final = snapshots.Last();
            AssertEqual(1, final.IncompleteVolumeCount);
            AssertEqual(1, final.IgnoreCount);
            AssertEqual(0, engine.ExtractionCalls);

            var absoluteWithoutExtension = Path.Combine(root, "absolute-name");
            File.WriteAllText(absoluteWithoutExtension + ".rar", "rar");
            var passwordBook = Path.Combine(root, "passwords.txt");
            File.WriteAllLines(passwordBook, [absoluteWithoutExtension, "password"]);
            var imported = new BatchOperationService(engine, new TestSystemIntegration())
                .LoadFilesFromTextFileWithDiagnostics(passwordBook, Path.Combine(root, "other"), "rar");
            AssertEqual(1, imported.RequestedCount);
            AssertEqual(1, imported.Entries.Count);
            AssertEqual(absoluteWithoutExtension + ".rar", imported.Entries[0].FilePath);
        }
        finally
        {
            Directory.Delete(root, true);
        }
    }

    private static async Task TestCompressedVolumeSizeProgress()
    {
        var root = Path.Combine(Path.GetTempPath(), $"batch-compress-output-size-{Guid.NewGuid():N}");
        var source = Path.Combine(root, "source.txt");
        var output = Path.Combine(root, "out");
        Directory.CreateDirectory(root);
        File.WriteAllText(source, "source");
        var snapshots = new List<OperationProgressInfo>();
        try
        {
            await new BatchOperationService(new VolumeWritingArchiveEngine(), new TestSystemIntegration())
                .BatchCompressAsync(
                    [source],
                    new BatchOperationOptions
                    {
                        OutputPath = output,
                        Extension = "7z",
                        ExistingFileMode = ExistingFileMode.Overwrite
                    },
                    new SnapshotProgress(snapshots),
                    CancellationToken.None);

            var final = snapshots.Last();
            var expected = 3072d / (1024d * 1024d * 1024d);
            Assert(Math.Abs(final.ProcessedSizeGB - expected) < 0.000000001,
                $"分卷统计必须累计全部卷大小，期望 {expected}，实际 {final.ProcessedSizeGB}");
        }
        finally
        {
            if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
        }
    }

    private static async Task TestFailedExtractionRollback()
    {
        var root = Path.Combine(Path.GetTempPath(), $"batch-compress-extraction-rollback-{Guid.NewGuid():N}");
        var archive = Path.Combine(root, "broken.7z");
        var existingOutput = Path.Combine(root, "existing-output");
        var freshOutput = Path.Combine(root, "fresh-output");
        Directory.CreateDirectory(existingOutput);
        File.WriteAllText(archive, "archive placeholder");
        File.WriteAllText(Path.Combine(existingOutput, "keep.txt"), "keep this file");
        try
        {
            var engine = new PartialFailureArchiveEngine();
            var snapshots = new List<OperationProgressInfo>();
            await new BatchOperationService(engine, new TestSystemIntegration()).BatchDecompressAsync(
                [new FileEntry { FilePath = archive, FileSize = new FileInfo(archive).Length }],
                new BatchOperationOptions { OutputPath = existingOutput, Extension = "7z" },
                new SnapshotProgress(snapshots),
                CancellationToken.None);

            AssertEqual(1, snapshots.Last().FailCount);
            Assert(File.Exists(Path.Combine(existingOutput, "keep.txt")), "解压失败不得删除输出目录中原有文件");
            Assert(!File.Exists(Path.Combine(existingOutput, "partial.bin")), "解压失败必须清理新增部分文件");
            Assert(!Directory.Exists(Path.Combine(existingOutput, "partial")), "解压失败必须清理新增目录");

            engine.Reset();
            snapshots.Clear();
            await new BatchOperationService(engine, new TestSystemIntegration()).BatchDecompressAsync(
                [new FileEntry { FilePath = archive, FileSize = new FileInfo(archive).Length }],
                new BatchOperationOptions { OutputPath = freshOutput, Extension = "7z" },
                new SnapshotProgress(snapshots),
                CancellationToken.None);

            AssertEqual(1, snapshots.Last().FailCount);
            Assert(!Directory.Exists(freshOutput), "新建输出目录在解压失败后不得残留空目录");
        }
        finally
        {
            if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
        }
    }

    private static async Task TestArchiveFolderScanning()
    {
        var root = Path.Combine(Path.GetTempPath(), $"batch-compress-folder-scan-{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);
        try
        {
            var first = Path.Combine(root, "group.zip.001");
            var second = Path.Combine(root, "group.zip.002");
            File.WriteAllText(first, "1");
            File.WriteAllText(second, "2");
            File.WriteAllText(Path.Combine(root, "other.rar"), "rar");
            File.WriteAllText(Path.Combine(root, "note.txt"), "text");
            Directory.CreateDirectory(Path.Combine(root, "folder"));

            var engine = new RecordingArchiveEngine();
            var service = new BatchOperationService(engine, new TestSystemIntegration());
            var archives = service.LoadArchivesFromFolder(root, "zip", skipProcessed: false);
            AssertEqual(1, archives.Count);
            AssertEqual(first, archives[0]);

            var compressionSources = service.LoadCompressionSourcesFromFolder(root, skipProcessed: false);
            Assert(compressionSources.Contains(Path.Combine(root, "folder")), "压缩来源扫描必须保留直接子目录");
            Assert(compressionSources.Contains(Path.Combine(root, "note.txt")), "压缩来源扫描不能按归档格式过滤");

            var snapshots = new List<OperationProgressInfo>();
            await service.BatchDecompressAsync(
                [
                    new FileEntry { FilePath = second, FileSize = 1 },
                    new FileEntry { FilePath = first, FileSize = 1 }
                ],
                new BatchOperationOptions { OutputPath = Path.Combine(root, "out"), Extension = "zip" },
                new SnapshotProgress(snapshots),
                CancellationToken.None);
            AssertEqual(1, engine.ExtractionCalls);
            AssertEqual(1, snapshots.Last().SuccessCount);
            AssertEqual(1, snapshots.Last().IgnoreCount);
        }
        finally
        {
            Directory.Delete(root, true);
        }
    }

    private static async Task TestAttachmentRootInputs()
    {
        var root = Path.Combine(Path.GetTempPath(), $"batch-compress-attachment-{Guid.NewGuid():N}");
        var source = Path.Combine(root, "source");
        var existing = Path.Combine(root, "外部附件");
        var missing = Path.Combine(root, "待创建附件");
        Directory.CreateDirectory(source);
        Directory.CreateDirectory(existing);
        File.WriteAllText(Path.Combine(existing, "attachment.txt"), "attachment");
        var engine = new RecordingArchiveEngine();
        try
        {
            var service = new BatchOperationService(engine, new TestSystemIntegration());
            await service.BatchCompressAsync([source], new BatchOperationOptions
            {
                OutputPath = Path.Combine(root, "out"), Extension = "7z", AddEnclosures = true,
                EnclosureDirectories = [existing, missing], ExistingFileMode = ExistingFileMode.Overwrite
            }, new Progress<OperationProgressInfo>(), CancellationToken.None);
            Assert(engine.LastOptions?.AdditionalInputs?.Any(path => Path.GetFileName(path) == "外部附件") == true,
                "存在的附件必须作为根级输入传递");
            Assert(engine.LastOptions?.AdditionalInputs?.Any(path => Path.GetFileName(path) == "待创建附件") == true,
                "不存在的附件必须在暂存目录创建根级空目录");
            Assert(!Directory.Exists(Path.Combine(source, "外部附件")), "不得把附件目录写入源目录");
            Assert(!Directory.Exists(Path.Combine(source, "待创建附件")), "不得把缺失附件目录写入源目录");
        }
        finally { if (Directory.Exists(root)) Directory.Delete(root, true); }
    }

    private static async Task TestExistingSkipProgress()
    {
        var root = Path.Combine(Path.GetTempPath(), $"batch-compress-skip-{Guid.NewGuid():N}");
        var source = Path.Combine(root, "source");
        var output = Path.Combine(root, "out");
        Directory.CreateDirectory(source);
        Directory.CreateDirectory(output);
        File.WriteAllText(Path.Combine(source, "a.txt"), "a");
        File.WriteAllText(Path.Combine(output, "source.7z"), "already");
        File.WriteAllText(Path.Combine(output, "source.7z.001"), "volume 1");
        File.WriteAllText(Path.Combine(output, "source.7z.002"), "volume 2");
        var engine = new RecordingArchiveEngine();
        var snapshots = new List<OperationProgressInfo>();
        try
        {
            await new BatchOperationService(engine, new TestSystemIntegration()).BatchCompressAsync(
                [source], new BatchOperationOptions { OutputPath = output, Extension = "7z", ExistingFileMode = ExistingFileMode.Skip },
                new SnapshotProgress(snapshots), CancellationToken.None);
            AssertEqual(1, snapshots.Last().IgnoreCount);
            AssertEqual(0, snapshots.Last().FailCount);
            AssertEqual(0, engine.CompressionCalls);
        }
        finally { if (Directory.Exists(root)) Directory.Delete(root, true); }
    }

    private static async Task TestPostProcessConflict()
    {
        var root = Path.Combine(Path.GetTempPath(), $"batch-compress-post-process-{Guid.NewGuid():N}");
        var source = Path.Combine(root, "source");
        var output = Path.Combine(root, "out");
        var processed = Path.Combine(root, "【已压缩】", "source");
        Directory.CreateDirectory(source);
        Directory.CreateDirectory(output);
        Directory.CreateDirectory(processed);
        File.WriteAllText(Path.Combine(source, "source.txt"), "source remains");
        File.WriteAllText(Path.Combine(processed, "existing.txt"), "target remains");
        var snapshots = new List<OperationProgressInfo>();
        try
        {
            await new BatchOperationService(new TestArchiveEngine(), new TestSystemIntegration()).BatchCompressAsync(
                [source],
                new BatchOperationOptions
                {
                    OutputPath = output,
                    Extension = "rar",
                    ExistingFileMode = ExistingFileMode.Overwrite,
                    MoveSourceAfter = true
                },
                new SnapshotProgress(snapshots),
                CancellationToken.None);

            var final = snapshots.Last();
            AssertEqual(1, final.SuccessCount);
            AssertEqual(0, final.FailCount);
            AssertEqual(1, final.PostProcessFailCount);
            // GPT-5, 2026-09-30：失败记录必须包含具体原因（含目标路径），而不是只有“后处理失败”。
            Assert(final.Message.Contains("【已压缩】"), "移动目标冲突原因必须进入失败记录消息");
            Assert(Directory.Exists(source), "移动目标冲突时必须保留源目录");
            Assert(File.Exists(Path.Combine(processed, "existing.txt")), "移动目标冲突时必须保留目标目录");

            var firstVolume = Path.Combine(root, "archive.7z.001");
            var secondVolume = Path.Combine(root, "archive.7z.002");
            var extractedProcessed = Path.Combine(root, "【已解压】");
            var conflictingSecondVolume = Path.Combine(extractedProcessed, "archive.7z.002");
            File.WriteAllText(firstVolume, "first volume");
            File.WriteAllText(secondVolume, "second volume");
            Directory.CreateDirectory(extractedProcessed);
            File.WriteAllText(conflictingSecondVolume, "existing target");
            snapshots.Clear();

            await new BatchOperationService(new TestArchiveEngine(), new TestSystemIntegration()).BatchDecompressAsync(
                [new FileEntry { FilePath = firstVolume, FileSize = new FileInfo(firstVolume).Length }],
                new BatchOperationOptions
                {
                    OutputPath = Path.Combine(root, "extracted"),
                    Extension = "7z",
                    ExistingFileMode = ExistingFileMode.Overwrite,
                    MoveSourceAfter = true
                },
                new SnapshotProgress(snapshots),
                CancellationToken.None);

            final = snapshots.Last();
            AssertEqual(1, final.SuccessCount);
            AssertEqual(0, final.FailCount);
            AssertEqual(1, final.PostProcessFailCount);
            // GPT-5, 2026-09-30：解压侧同样要求原因可见，且包含冲突分卷的具体路径。
            Assert(final.Message.Contains("archive.7z.002"), "解压目标冲突原因必须进入失败记录消息");
            Assert(File.Exists(firstVolume) && File.Exists(secondVolume), "任一分卷目标冲突时必须保留整组源卷");
            Assert(!File.Exists(Path.Combine(extractedProcessed, "archive.7z.001")), "分卷冲突时不得移动部分分卷");
            AssertEqual("existing target", File.ReadAllText(conflictingSecondVolume));
        }
        finally { if (Directory.Exists(root)) Directory.Delete(root, true); }
    }
}
