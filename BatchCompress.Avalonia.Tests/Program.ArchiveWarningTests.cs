using System.Collections.Generic;
using BatchCompress.Avalonia.Core.Interfaces;
using BatchCompress.Avalonia.Core.Models;
using BatchCompress.Avalonia.Core.Services;
using BatchCompress.Avalonia.Localization;

internal static partial class Program
{
    // Claude Opus 5.5, 2026-11-20：0.8.20——归档程序返回码 1（警告）时不得删除/移动来源。
    // 0.8.22：真实引擎压缩时返回码 1 直接判失败（场景 6）；场景 1–5 用模拟引擎锁定管线兜底拦截，
    // 覆盖解压警告以及任何返回“成功但有警告”的引擎。
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

            // 场景 6：真实 7zz（仅 macOS）——0.8.22 起压缩返回码 1 直接判失败。
            if (OperatingSystem.IsMacOS())
            {
                var executable = Path.Combine(AppContext.BaseDirectory, "tools", "7zip", "macos", "7zz");
                var locked = Path.Combine(root, "locked.txt");
                File.WriteAllText(locked, "secret");
                File.SetUnixFileMode(locked, UnixFileMode.None);
                try
                {
                    // 6a：引擎层——校验开启时也必须返回失败，错误信息带上 7zz 的警告内容。
                    var result = await new SevenZipArchiveEngine(executable).CompressAsync(
                        locked,
                        Path.Combine(root, "engine-out", "locked.txt.7z"),
                        new ArchiveOptions { ArchiveFormat = "7z", TestArchive = true });
                    Assert(!result.Success, "压缩返回码 1 必须判失败");
                    AssertEqual(SevenZipExitCodes.Warning, result.ExitCode);
                    Assert(result.ErrorMessage?.Contains("压缩不完整", StringComparison.Ordinal) == true,
                        "失败原因必须说明压缩不完整");
                    Assert(result.ErrorMessage?.Contains("Permission denied", StringComparison.Ordinal) == true,
                        "失败原因必须带上 7zz 的警告内容");

                    // 6b：完整管线——覆盖模式 + 删除源：计失败、来源保留、不完整归档被删除、旧归档恢复。
                    var pipelineOut = Path.Combine(root, "pipeline-out");
                    Directory.CreateDirectory(pipelineOut);
                    var oldArchive = Path.Combine(pipelineOut, "locked.txt.7z");
                    File.WriteAllText(oldArchive, "old archive");
                    var realSnapshots = new List<OperationProgressInfo>();
                    await new BatchOperationService(new SevenZipArchiveEngine(executable), new TestSystemIntegration())
                        .BatchCompressAsync(
                            [locked],
                            new BatchOperationOptions
                            {
                                OutputPath = pipelineOut,
                                Extension = "7z",
                                ExistingFileMode = ExistingFileMode.Overwrite,
                                DeleteSourceAfter = true,
                                TestArchive = true
                            },
                            new SnapshotProgress(realSnapshots),
                            CancellationToken.None);
                    var realFinal = realSnapshots.Last();
                    AssertEqual(0, realFinal.SuccessCount);
                    AssertEqual(1, realFinal.FailCount);
                    Assert(File.Exists(locked), "压缩不完整时不得删除来源");
                    AssertEqual("old archive", File.ReadAllText(oldArchive));
                    AssertEqual(1, Directory.GetFiles(pipelineOut).Length);
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

    // Gemini 4 Neon, 2026-11-26 / 2026-11-27：批次 36–37——验证 --strict-extract 与解压警告分级策略。
    private static Task TestStrictExtractPolicyAndCli()
    {
        var ignorableWarning = new ArchiveResult
        {
            Success = true,
            HasWarnings = true,
            ExitCode = 1,
            StandardOutput = "WARNINGS:\nThere are data after the end of archive\nEverything is Ok\nWarnings: 1"
        };

        var defaultResult = CompressionWarningPolicy.ApplyForExtraction(ignorableWarning, "7-Zip", strictExtract: false);
        Assert(ReferenceEquals(ignorableWarning, defaultResult), "默认模式下纯可忽略元数据警告应原样放行");
        Assert(defaultResult.Success, "默认模式下纯可忽略警告仍应计为成功");

        var strictResult = CompressionWarningPolicy.ApplyForExtraction(ignorableWarning, "7-Zip", strictExtract: true);
        Assert(!ReferenceEquals(ignorableWarning, strictResult), "严格模式下可忽略警告也应返回新失败实例");
        Assert(ignorableWarning.Success, "原结果实例不得被修改");
        Assert(!strictResult.Success, "严格模式下可忽略警告必须判失败");
        Assert(strictResult.ErrorMessage?.Contains("解压不完整", StringComparison.Ordinal) == true,
            "严格模式失败说明必须包含解压不完整");

        var parsedStrict = BatchCompress.Avalonia.CommandLineHandler.ParseArguments(
            ["extract", "--input", "a.7z", "--output", "out", "--strict-extract"]);
        Assert(parsedStrict.Success, "带 --strict-extract 的参数应校验通过");
        Assert(parsedStrict.Options.StrictExtract, "解析结果应启用 StrictExtract");

        var parsedDefault = BatchCompress.Avalonia.CommandLineHandler.ParseArguments(
            ["extract", "--input", "a.7z", "--output", "out"]);
        Assert(parsedDefault.Success, "默认 extract 参数应校验通过");
        Assert(!parsedDefault.Options.StrictExtract, "未传 --strict-extract 时 StrictExtract 应默认为 false");

        return Task.CompletedTask;
    }

    // Gemini 4 Neon, 2026-11-27：批次 37——验证解压返回码 1 智能分级（重大问题失败回滚、可忽略放行且方案 A 保原包、未知保守判失败）。
    private static async Task TestExtractionWarningClassificationAndPipeline()
    {
        // 1. 重大写入/权限/占用错误：默认即判 Critical 失败
        foreach (var errLine in new[]
        {
            "ERROR: Cannot open output file : errno=13 : Permission denied : /tmp/out/a.txt",
            "WARNING: Cannot create output file: Access is denied",
            "The process cannot access the file because it is being used by another process.",
            "另一个程序正在使用此文件，进程无法访问。"
        })
        {
            var r = new ArchiveResult { Success = true, HasWarnings = true, ExitCode = 1, StandardError = errLine };
            var assessment = ExtractionWarningClassifier.Assess(r);
            AssertEqual(ExtractionWarningSeverity.Critical, assessment.Severity);
            var applied = CompressionWarningPolicy.ApplyForExtraction(r, "7-Zip", strictExtract: false);
            Assert(!applied.Success, $"默认模式对重大写入错误必须判失败: {errLine}");
        }

        // 2. 文件名/路径限制：默认即判 Critical 失败
        foreach (var pathErr in new[]
        {
            "ERROR: File name too long : very_long_name.txt",
            "Path too long: /tmp/out/nested/file.bin",
            "Cannot open output file : Illegal byte sequence"
        })
        {
            var r = new ArchiveResult { Success = true, HasWarnings = true, ExitCode = 1, StandardError = pathErr };
            var assessment = ExtractionWarningClassifier.Assess(r);
            AssertEqual(ExtractionWarningSeverity.Critical, assessment.Severity);
            var applied = CompressionWarningPolicy.ApplyForExtraction(r, "7-Zip", strictExtract: false);
            Assert(!applied.Success, $"默认模式对路径限制错误必须判失败: {pathErr}");
        }

        // 3. 可忽略跨平台与元数据警告：默认 Ignorable 放行，严格模式判失败
        var realTrailingStdout = """
            7-Zip (z) 26.05 (arm64) : Copyright (c) 1999-2026 Igor Pavlov : 2026-10-21

            Extracting archive: /tmp/trailing.7z

            WARNINGS:
            There are data after the end of archive

            --
            Path = /tmp/trailing.7z
            Type = 7z

            Everything is Ok

            Archives with Warnings: 1

            Warnings: 1
            """;
        foreach (var ignorable in new[]
        {
            new ArchiveResult { Success = true, HasWarnings = true, ExitCode = 1, StandardOutput = realTrailingStdout },
            new ArchiveResult { Success = true, HasWarnings = true, ExitCode = 1, StandardError = "WARNING: Dangerous link path was ignored for link -> /etc/passwd" },
            new ArchiveResult { Success = true, HasWarnings = true, ExitCode = 1, StandardError = "WARNING: Cannot create symbolic link : A required privilege is not held by the client." },
            new ArchiveResult { Success = true, HasWarnings = true, ExitCode = 1, StandardError = "WARNING: Cannot set file attribute\nWARNING: Cannot set file time" }
        })
        {
            var assessment = ExtractionWarningClassifier.Assess(ignorable);
            AssertEqual(ExtractionWarningSeverity.Ignorable, assessment.Severity);
            Assert(CompressionWarningPolicy.ApplyForExtraction(ignorable, "7-Zip", strictExtract: false).Success,
                "默认模式应对纯可忽略警告放行");
            Assert(!CompressionWarningPolicy.ApplyForExtraction(ignorable, "7-Zip", strictExtract: true).Success,
                "严格模式应对可忽略警告判失败");
        }

        // 4. 混合警告、未知警告与空输出：一律保守判 Critical 失败
        var mixed = new ArchiveResult
        {
            Success = true,
            HasWarnings = true,
            ExitCode = 1,
            StandardOutput = "WARNINGS:\nThere are data after the end of archive",
            StandardError = "ERROR: Cannot open output file : /tmp/locked.bin"
        };
        AssertEqual(ExtractionWarningSeverity.Critical, ExtractionWarningClassifier.Assess(mixed).Severity);

        var unknown = new ArchiveResult
        {
            Success = true,
            HasWarnings = true,
            ExitCode = 1,
            StandardOutput = "WARNINGS:\nThere are data after the end of archive",
            StandardError = "WARNING: unexpected custom issue"
        };
        AssertEqual(ExtractionWarningSeverity.Critical, ExtractionWarningClassifier.Assess(unknown).Severity);

        var empty = new ArchiveResult { Success = true, HasWarnings = true, ExitCode = 1 };
        AssertEqual(ExtractionWarningSeverity.Critical, ExtractionWarningClassifier.Assess(empty).Severity);

        // 5. 完整管线验证（方案 A）：
        var root = Path.Combine(Path.GetTempPath(), $"bc-extract-classify-{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);
        try
        {
            // 5a. 重大写入警告（默认模式 + 删除源）：计失败、残片已回滚、原包保留
            var criticalArchive = Path.Combine(root, "critical.7z");
            var criticalOut = Path.Combine(root, "critical-out");
            File.WriteAllText(criticalArchive, "arc");
            var s1 = new List<OperationProgressInfo>();
            await new BatchOperationService(
                    new ClassifiedWarningExtractEngine("ERROR: Cannot open output file : locked.txt"),
                    new TestSystemIntegration())
                .BatchDecompressAsync(
                    [new FileEntry { FilePath = criticalArchive, FileSize = 3 }],
                    new BatchOperationOptions { OutputPath = criticalOut, DeleteSourceAfter = true },
                    new SnapshotProgress(s1),
                    CancellationToken.None);
            AssertEqual(0, s1.Last().SuccessCount);
            AssertEqual(1, s1.Last().FailCount);
            Assert(!File.Exists(Path.Combine(criticalOut, "part.txt")), "重大警告判失败后必须回滚解出残片");
            Assert(File.Exists(criticalArchive), "重大警告失败后必须保留原压缩包");

            // 5b. 可忽略警告（默认模式 + 删除源，方案 A）：计成功 + 后处理失败，解出文件保留，原包按方案 A 保留
            var ignorableArchive = Path.Combine(root, "ignorable.7z");
            var ignorableOut = Path.Combine(root, "ignorable-out");
            File.WriteAllText(ignorableArchive, "arc");
            var s2 = new List<OperationProgressInfo>();
            await new BatchOperationService(
                    new ClassifiedWarningExtractEngine("WARNING: Cannot create symbolic link : foo"),
                    new TestSystemIntegration())
                .BatchDecompressAsync(
                    [new FileEntry { FilePath = ignorableArchive, FileSize = 3 }],
                    new BatchOperationOptions { OutputPath = ignorableOut, DeleteSourceAfter = true },
                    new SnapshotProgress(s2),
                    CancellationToken.None);
            AssertEqual(1, s2.Last().SuccessCount);
            AssertEqual(0, s2.Last().FailCount);
            AssertEqual(1, s2.Last().PostProcessFailCount);
            Assert(File.Exists(Path.Combine(ignorableOut, "part.txt")), "可忽略警告默认模式下应保留已解出文件");
            Assert(File.Exists(ignorableArchive), "方案 A：即使可忽略警告放行，开启删除源时仍必须保留原压缩包");

            // 5c. 可忽略警告（严格模式 + 删除源）：判失败、残片回滚、原包保留
            var strictArchive = Path.Combine(root, "strict.7z");
            var strictOut = Path.Combine(root, "strict-out");
            File.WriteAllText(strictArchive, "arc");
            var s3 = new List<OperationProgressInfo>();
            await new BatchOperationService(
                    new ClassifiedWarningExtractEngine("WARNING: Cannot create symbolic link : foo"),
                    new TestSystemIntegration())
                .BatchDecompressAsync(
                    [new FileEntry { FilePath = strictArchive, FileSize = 3 }],
                    new BatchOperationOptions { OutputPath = strictOut, DeleteSourceAfter = true, StrictExtract = true },
                    new SnapshotProgress(s3),
                    CancellationToken.None);
            AssertEqual(0, s3.Last().SuccessCount);
            AssertEqual(1, s3.Last().FailCount);
            Assert(!File.Exists(Path.Combine(strictOut, "part.txt")), "严格模式下可忽略警告也必须回滚解出文件");
            Assert(File.Exists(strictArchive), "严格模式失败后必须保留原压缩包");
        }
        finally
        {
            if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
        }
    }

    private sealed class ClassifiedWarningExtractEngine(string stderrWarning) : IArchiveEngine
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
            Directory.CreateDirectory(outputDir);
            File.WriteAllText(Path.Combine(outputDir, "part.txt"), "partial");
            var raw = new ArchiveResult
            {
                Success = true,
                HasWarnings = true,
                ExitCode = 1,
                StandardError = stderrWarning
            };
            return Task.FromResult(CompressionWarningPolicy.ApplyForExtraction(raw, "7-Zip", options.StrictExtract));
        }
    }
}
