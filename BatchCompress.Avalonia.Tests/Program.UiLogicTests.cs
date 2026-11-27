using System;
using System.IO;
using System.Threading.Tasks;
using BatchCompress.Avalonia.Core.Models;
using BatchCompress.Avalonia.Core.Services;
using BatchCompress.Avalonia.ViewModels;

// WorkBuddy, 2026-11-16：0.8.13——界面层可测试性：
// 把不依赖界面框架的派生逻辑抽出来做真实单元测试，并用源码守护锁住属性通知与释放逻辑。
internal static partial class Program
{
    private static Task TestTabVisibilityDerivation()
    {
        // 0=压缩配置、1=解压配置、2=开始、3=成功记录、4=失败记录、5=命令日志。
        Assert(TabVisibility.IsCompressionActionVisible(0), "压缩配置页应显示压缩按钮");
        Assert(!TabVisibility.IsDecompressionActionVisible(0), "压缩配置页不应显示解压按钮");

        Assert(!TabVisibility.IsCompressionActionVisible(1), "解压配置页不应显示压缩按钮");
        Assert(TabVisibility.IsDecompressionActionVisible(1), "解压配置页应显示解压按钮");

        Assert(TabVisibility.IsCompressionActionVisible(2), "开始页应显示压缩按钮");
        Assert(TabVisibility.IsDecompressionActionVisible(2), "开始页应显示解压按钮");

        foreach (var tab in new[] { 3, 4, 5 })
        {
            Assert(!TabVisibility.IsCompressionActionVisible(tab), $"日志页 {tab} 不应显示压缩按钮");
            Assert(!TabVisibility.IsDecompressionActionVisible(tab), $"日志页 {tab} 不应显示解压按钮");
        }

        // 属性通知必须同时声明这两个派生属性，否则切页时绑定不会刷新。
        var viewModel = File.ReadAllText(
            Path.Combine(FindRepositoryRoot(), "ViewModels", "MainWindowViewModel.cs"));
        Assert(viewModel.Contains("NotifyPropertyChangedFor(nameof(IsCompressionActionVisible))", StringComparison.Ordinal),
            "ActiveTab 变化时未通知压缩按钮显隐属性");
        Assert(viewModel.Contains("NotifyPropertyChangedFor(nameof(IsDecompressionActionVisible))", StringComparison.Ordinal),
            "ActiveTab 变化时未通知解压按钮显隐属性");

        return Task.CompletedTask;
    }

    private static Task TestUiLifecycleWiring()
    {
        var root = FindRepositoryRoot();

        // 语言服务是静态单例：必须能精确退订，否则关闭后的 ViewModel 永远不会被释放。
        var lifecycle = Path.Combine(root, "ViewModels", "MainWindowViewModel.Lifecycle.cs");
        Assert(File.Exists(lifecycle), "缺少 ViewModel 释放逻辑文件");
        var lifecycleText = File.ReadAllText(lifecycle);
        Assert(lifecycleText.Contains("PropertyChanged -= _languageChangedHandler", StringComparison.Ordinal),
            "释放时未退订语言服务事件");
        Assert(lifecycleText.Contains("_fileLogger?.Dispose()", StringComparison.Ordinal),
            "释放时未释放界面文件日志器");

        // 窗口关闭与数据上下文切换都要触发释放，且旧实例要退订属性变化。
        var window = File.ReadAllText(Path.Combine(root, "Views", "MainWindow.axaml.cs"));
        Assert(window.Contains("_boundViewModel?.Dispose()", StringComparison.Ordinal),
            "窗口关闭时未释放 ViewModel");
        Assert(window.Contains("previous.PropertyChanged -= ViewModel_PropertyChanged", StringComparison.Ordinal),
            "切换数据上下文时未退订旧实例的属性变化");

        // 零散日志追加后必须有一次性的尾刷，否则节流窗口内最后一行可能不显示。
        var logging = File.ReadAllText(Path.Combine(root, "ViewModels", "MainWindowViewModel.Logging.cs"));
        Assert(logging.Contains("ScheduleTailFlush", StringComparison.Ordinal),
            "日志追加缺少尾刷调度");
        Assert(logging.Contains("DispatcherTimer.RunOnce", StringComparison.Ordinal),
            "尾刷应使用一次性定时器，避免常驻定时器");

        // 执行中关窗必须拦截、确认、取消并等待清理，而不是直接 Dispose 退出。
        Assert(window.Contains("e.Cancel = true", StringComparison.Ordinal),
            "执行中关窗未取消默认关闭");
        Assert(window.Contains("CancelOperationCommand.Execute", StringComparison.Ordinal),
            "关窗确认后未请求取消任务");
        Assert(window.Contains("WaitForOperationCompletionAsync", StringComparison.Ordinal),
            "关窗后未等待操作收尾");
        Assert(window.Contains("ConfirmCloseDuringOperationAsync", StringComparison.Ordinal),
            "缺少执行中关窗确认方法");

        var app = File.ReadAllText(Path.Combine(root, "App.axaml.cs"));
        Assert(app.Contains("window.Close()", StringComparison.Ordinal),
            "托盘退出未走窗口关闭保护路径");
        Assert(app.Contains("desktop.Exit += OnDesktopExit", StringComparison.Ordinal) &&
               app.Contains("PropertyChanged -= OnLocalizationServicePropertyChanged", StringComparison.Ordinal),
            "App.axaml.cs 应在桌面退出时退订静态语言监听");

        var cliHandler = Path.Combine(root, "CommandLineHandler.cs");
        var cliValidation = Path.Combine(root, "CommandLineHandler.Validation.cs");
        Assert(File.Exists(cliValidation), "缺少 CommandLineHandler.Validation.cs 拆解文件");
        Assert(File.ReadAllLines(cliHandler).Length < 700, "CommandLineHandler.cs 应保持拆解后精简（< 700 行）");

        return Task.CompletedTask;
    }

    private static Task TestDirectorySizeSkipsSymlinkLoops()
    {
        var root = Path.Combine(Path.GetTempPath(), $"directory-size-{Guid.NewGuid():N}");
        var sub = Path.Combine(root, "sub");
        Directory.CreateDirectory(sub);
        File.WriteAllText(Path.Combine(root, "a.txt"), "abc");
        File.WriteAllText(Path.Combine(sub, "b.txt"), "12345");

        string? loop = null;
        try
        {
            var normal = DirectorySizeCalculator.Calculate(root);
            AssertEqual(8L, normal.Size);
            AssertEqual(0, normal.Errors.Count);

            if (!OperatingSystem.IsWindows())
            {
                loop = Path.Combine(root, "loop");
                Directory.CreateSymbolicLink(loop, root);
                var linked = DirectorySizeCalculator.Calculate(root);
                AssertEqual(8L, linked.Size);
                AssertEqual(0, linked.Errors.Count);
            }
        }
        finally
        {
            if (loop != null && Directory.Exists(loop))
            {
                Directory.Delete(loop);
            }

            if (Directory.Exists(root))
            {
                Directory.Delete(root, recursive: true);
            }
        }

        return Task.CompletedTask;
    }

    private static Task TestOperationCompletionSignal()
    {
        var signal = new OperationCompletionSignal();
        Assert(signal.WaitAsync().IsCompleted, "没有进行中的操作时应立即完成");

        signal.Begin();
        var wait = signal.WaitAsync();
        Assert(!wait.IsCompleted, "操作开始后等待任务不应提前完成");

        signal.Complete();
        Assert(wait.IsCompletedSuccessfully, "操作结束后等待任务应完成");

        // 第二次操作必须得到新的等待任务，不能复用已完成的 Task。
        signal.Begin();
        var second = signal.WaitAsync();
        Assert(!second.IsCompleted, "第二次操作开始后应创建新的等待任务");
        signal.Complete();
        Assert(second.IsCompletedSuccessfully, "第二次操作结束后等待任务应完成");

        return Task.CompletedTask;
    }

    // Gemini 4 Neon, 2026-11-26：批次 34——界面“删除源”勾选框二次确认状态协调与接线守护。
    private static async Task TestDeleteSourceConfirmation()
    {
        var guard = new DeleteSourceConfirmationGuard();

        Assert(await guard.ConfirmEnableAsync(null), "未注入确认回调时应允许启用");
        Assert(!guard.IsConfirming, "未注入回调结束后不应处于确认中");

        Assert(await guard.ConfirmEnableAsync(() => Task.FromResult(true)), "用户确认时应返回 true");
        Assert(!guard.IsConfirming, "确认完成后状态必须复位");

        Assert(!await guard.ConfirmEnableAsync(() => Task.FromResult(false)), "用户取消时应返回 false");
        Assert(!guard.IsConfirming, "取消完成后状态必须复位");

        Assert(!await guard.ConfirmEnableAsync(() => throw new InvalidOperationException("boom")), "确认回调异常时应安全返回 false");
        Assert(!guard.IsConfirming, "异常后状态必须复位");

        var tcs = new TaskCompletionSource<bool>();
        var pending = guard.ConfirmEnableAsync(() => tcs.Task);
        Assert(guard.IsConfirming, "确认进行中时 IsConfirming 必须为 true");
        Assert(!await guard.ConfirmEnableAsync(() => Task.FromResult(true)), "确认进行中重入调用必须返回 false");
        tcs.SetResult(true);
        Assert(await pending, "原确认任务完成时应返回用户结果");
        Assert(!guard.IsConfirming, "全部完成后状态必须复位");

        var root = FindRepositoryRoot();
        var viewModelText = File.ReadAllText(Path.Combine(root, "ViewModels", "MainWindowViewModel.cs"));
        Assert(viewModelText.Contains("ConfirmDeleteSourceRequested", StringComparison.Ordinal),
            "MainWindowViewModel 缺少 ConfirmDeleteSourceRequested 回调");
        Assert(viewModelText.Contains("_deleteSourceConfirmationGuard", StringComparison.Ordinal),
            "MainWindowViewModel 缺少 _deleteSourceConfirmationGuard");
        Assert(viewModelText.Contains("DeleteSourceAfter = false", StringComparison.Ordinal),
            "MainWindowViewModel 取消确认时未回退 DeleteSourceAfter");

        var windowText = File.ReadAllText(Path.Combine(root, "Views", "MainWindow.axaml.cs"));
        Assert(windowText.Contains("ConfirmDeleteSourceRequested = ConfirmDeleteSourceAsync", StringComparison.Ordinal),
            "MainWindow 未接线 ConfirmDeleteSourceRequested");
        Assert(windowText.Contains("L.ConfirmDeleteSourceTitle", StringComparison.Ordinal),
            "MainWindow 未使用 ConfirmDeleteSourceTitle");
        Assert(windowText.Contains("L.ConfirmDeleteSourceBody", StringComparison.Ordinal),
            "MainWindow 未使用 ConfirmDeleteSourceBody");
        Assert(windowText.Contains("L.ConfirmDeleteSourceEnable", StringComparison.Ordinal),
            "MainWindow 未使用 ConfirmDeleteSourceEnable");

        var strings = BatchCompress.Avalonia.Localization.LocalizationService.Instance.Strings;
        Assert(!string.IsNullOrWhiteSpace(strings.ConfirmDeleteSourceTitle), "ConfirmDeleteSourceTitle 不能为空");
        Assert(!string.IsNullOrWhiteSpace(strings.ConfirmDeleteSourceBody), "ConfirmDeleteSourceBody 不能为空");
        Assert(!string.IsNullOrWhiteSpace(strings.ConfirmDeleteSourceEnable), "ConfirmDeleteSourceEnable 不能为空");
    }

    // Gemini 4 Neon, 2026-11-26：批次 36——界面日志实时搜索过滤与快捷键接线守护。
    private static Task TestLogSearchFilter()
    {
        AssertEqual(string.Empty, LogSearchFilter.FilterLines(null, "err"));
        AssertEqual(string.Empty, LogSearchFilter.FilterLines(string.Empty, "err"));

        var sample = "INFO start\r\nERROR disk full\nwarn skipped\nError timeout";
        AssertEqual(sample, LogSearchFilter.FilterLines(sample, "   "));
        AssertEqual(
            $"ERROR disk full{Environment.NewLine}Error timeout",
            LogSearchFilter.FilterLines(sample, " error "));
        AssertEqual(string.Empty, LogSearchFilter.FilterLines(sample, "not-found"));

        var root = FindRepositoryRoot();
        var axaml = File.ReadAllText(Path.Combine(root, "Views", "MainWindow.axaml"));
        Assert(axaml.Contains("FilteredSuccessLog", StringComparison.Ordinal), "MainWindow.axaml 缺少 FilteredSuccessLog");
        Assert(axaml.Contains("FilteredFailLog", StringComparison.Ordinal), "MainWindow.axaml 缺少 FilteredFailLog");
        Assert(axaml.Contains("FilteredCommandLog", StringComparison.Ordinal), "MainWindow.axaml 缺少 FilteredCommandLog");
        Assert(axaml.Contains("Gesture=\"Ctrl+F\"", StringComparison.Ordinal), "MainWindow.axaml 缺少 Ctrl+F 快捷键");
        Assert(axaml.Contains("StrictExtract", StringComparison.Ordinal), "MainWindow.axaml 缺少 StrictExtract 勾选框");

        var codeBehind = File.ReadAllText(Path.Combine(root, "Views", "MainWindow.axaml.cs"));
        Assert(codeBehind.Contains("FocusLogSearchRequested = FocusCurrentLogSearchBox", StringComparison.Ordinal),
            "MainWindow.axaml.cs 未接线 FocusLogSearchRequested");

        return Task.CompletedTask;
    }

    // Gemini 4 Neon, 2026-11-27：批次 38——失败项一键加载重试纯逻辑与界面接线守护。
    private static Task TestFailedItemRetryList()
    {
        var retry = new FailedItemRetryList();
        retry.Record(new OperationProgressInfo { FailCount = 0, CurrentSourcePath = "/tmp/ok.txt" });
        retry.Record(new OperationProgressInfo { FailCount = 1, CurrentSourcePath = "/tmp/fail1.txt", IsCommandOutput = true });
        retry.Record(new OperationProgressInfo { FailCount = 1, CurrentSourcePath = "/tmp/fail1.txt" });
        retry.Record(new OperationProgressInfo { FailCount = 1, CurrentSourcePath = "/tmp/fail1.txt" }); // 重复增量不重复加
        retry.Record(new OperationProgressInfo { FailCount = 2, CurrentSourcePath = "   " }); // 空路径跳过
        retry.Record(new OperationProgressInfo { FailCount = 3, CurrentSourcePath = "/tmp/fail2.txt" });

        var failed = retry.GetFailedPaths();
        AssertEqual(2, failed.Count);
        AssertEqual("/tmp/fail1.txt", failed[0]);
        AssertEqual("/tmp/fail2.txt", failed[1]);

        var plainText = FailedItemRetryList.BuildRetrySourceText(failed, null, isPasswordBookMode: false);
        AssertEqual($"/tmp/fail1.txt{Environment.NewLine}/tmp/fail2.txt", plainText);

        var book = "/tmp/ok.7z\npw_ok\n/tmp/fail1.7z\npw_one\n/tmp/fail2.7z\npw_two";
        var bookRetry = FailedItemRetryList.BuildRetrySourceText(
            ["/tmp/fail1.7z", "/tmp/fail2.7z"], book, isPasswordBookMode: true);
        AssertEqual($"/tmp/fail1.7z{Environment.NewLine}pw_one{Environment.NewLine}/tmp/fail2.7z{Environment.NewLine}pw_two", bookRetry);

        var root = FindRepositoryRoot();
        var axaml = File.ReadAllText(Path.Combine(root, "Views", "MainWindow.axaml"));
        Assert(axaml.Split("LoadFailedItemsCommand").Length - 1 >= 2,
            "MainWindow.axaml 应在开始页和失败记录页都提供 LoadFailedItemsCommand 按钮");
        Assert(axaml.Contains("L.LoadFailedItems", StringComparison.Ordinal), "MainWindow.axaml 缺少 L.LoadFailedItems");
        Assert(axaml.Contains("L.TooltipLoadFailedItems", StringComparison.Ordinal), "MainWindow.axaml 缺少 L.TooltipLoadFailedItems");

        return Task.CompletedTask;
    }

    // Gemini 4 Hydrogen, 2026-11-27：批次 42——等价命令行生成器（安全脱敏、CLI 解析闭环及界面按钮与悬停提示接线守护）。
    private static Task TestCliCommandBuilder()
    {
        var work = Path.Combine(Path.GetTempPath(), "bc-cli-builder-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(work);
        try
        {
            var srcDir = Path.Combine(work, "source folder");
            var outDir = Path.Combine(work, "output folder");
            var commentFile = Path.Combine(work, "note.txt");
            var bookFile = Path.Combine(work, "passwords.txt");
            var enclosureDir = Path.Combine(work, "enc dir");
            Directory.CreateDirectory(srcDir);
            Directory.CreateDirectory(enclosureDir);
            File.WriteAllText(commentFile, "comment");
            File.WriteAllText(bookFile, "a.rar\npw");

            // 1. 压缩目录模式：生成参数经 CommandLineHandler.ParseArguments 解析必须完全通过并等价还原。
            var compressArgs = CliCommandBuilder.BuildArguments(new CliCommandBuildRequest
            {
                IsCompression = true,
                SourceMode = 1,
                SaveFilePath = srcDir,
                OutputPath = outDir,
                Extension = "7z",
                UseRandomPassword = true,
                PasswordNameMode = 1,
                CompressionLevel = 5,
                SolidArchive = false,
                EnableVolume = true,
                VolumeSize = "2",
                VolumeUnit = 1,
                RecoveryRecordPercent = 0,
                ExistingFileMode = 0,
                TestArchive = true,
                EnableComment = true,
                CommentFilePath = commentFile,
                SkipAlreadyProcessed = false,
                MaxSizeGB = 120,
                MaxConcurrency = 4,
                AddEnclosures = true,
                EnclosureDirectories = [enclosureDir],
                Language = "en"
            });

            var parsedCompress = BatchCompress.Avalonia.CommandLineHandler.ParseArguments(compressArgs.ToArray());
            Assert(parsedCompress.Success,
                $"压缩 CLI 生成参数必须合法：{string.Join(" | ", parsedCompress.Errors)}");
            Assert(parsedCompress.Options.Compress, "应解析为压缩模式");
            AssertEqual(srcDir, parsedCompress.Options.SourcePath);
            AssertEqual(outDir, parsedCompress.Options.OutputPath);
            AssertEqual("7z", parsedCompress.Options.Extension);
            AssertEqual("base", parsedCompress.Options.PasswordName);
            AssertEqual(5, parsedCompress.Options.CompressionLevel);
            Assert(!parsedCompress.Options.Solid, "SolidArchive=false 时应生成 --no-solid");
            AssertEqual("2", parsedCompress.Options.VolumeSize);
            AssertEqual("m", parsedCompress.Options.VolumeUnit);
            AssertEqual(0, parsedCompress.Options.RecoveryRecord);
            AssertEqual("skip", parsedCompress.Options.ExistingFileMode);
            Assert(parsedCompress.Options.TestArchive, "TestArchive=true 时应生成 --test");
            AssertEqual(commentFile, parsedCompress.Options.CommentFile);
            Assert(!parsedCompress.Options.SkipProcessed, "SkipAlreadyProcessed=false 时应生成 --no-skip-processed");
            AssertEqual(120d, parsedCompress.Options.MaxSizeGB);
            AssertEqual(4, parsedCompress.Options.MaxConcurrency);
            AssertEqual("en", parsedCompress.Options.Language);
            AssertEqual(1, parsedCompress.Options.EnclosurePaths.Length);
            AssertEqual(enclosureDir, parsedCompress.Options.EnclosurePaths[0]);

            var formatted = CliCommandBuilder.FormatCommand(new CliCommandBuildRequest
            {
                IsCompression = true,
                SourceMode = 1,
                SaveFilePath = srcDir,
                OutputPath = outDir
            });
            Assert(formatted.Contains($"\"{srcDir}\"", StringComparison.Ordinal),
                "含空格路径格式化时必须加引号");

            // 2. 解压密码本 + 自定义密码安全模式 + 严格解压：明文自定义密码绝不可出现，改用 --password-stdin。
            var extractReq = new CliCommandBuildRequest
            {
                IsCompression = false,
                SourceMode = 0,
                SaveFilePath = srcDir,
                SourcePath = bookFile,
                OutputPath = outDir,
                Extension = "rar",
                UseRandomPassword = false,
                HasCustomPassword = true,
                StrictExtract = true,
                MoveSourceAfter = true
            };
            var extractArgs = CliCommandBuilder.BuildArguments(extractReq);
            var parsedExtract = BatchCompress.Avalonia.CommandLineHandler.ParseArguments(extractArgs.ToArray());
            Assert(parsedExtract.Success,
                $"解压 CLI 生成参数必须合法：{string.Join(" | ", parsedExtract.Errors)}");
            Assert(parsedExtract.Options.Decompress, "应解析为解压模式");
            Assert(!parsedExtract.Options.UseRandomPassword, "UseRandomPassword=false 应禁用文件名密码");
            Assert(parsedExtract.Options.ReadPasswordFromStandardInput, "自定义密码应转为 --password-stdin 避免泄露");
            Assert(parsedExtract.Options.Password is null, "生成结果不得包含明文密码");
            Assert(parsedExtract.Options.StrictExtract, "StrictExtract=true 应包含 --strict-extract");
            Assert(parsedExtract.Options.MoveSource, "MoveSourceAfter=true 应包含 --move-source");

            // 3. 压缩 TXT 清单模式应映射为 --input（绝不能生成仅限解压的 --text-file）。
            var txtCompressArgs = CliCommandBuilder.BuildArguments(new CliCommandBuildRequest
            {
                IsCompression = true,
                SourceMode = 0,
                OutputPath = outDir,
                SourceListItems = [commentFile, bookFile]
            });
            var parsedTxtCompress = BatchCompress.Avalonia.CommandLineHandler.ParseArguments(txtCompressArgs.ToArray());
            Assert(parsedTxtCompress.Success,
                $"压缩 TXT 模式生成参数必须合法：{string.Join(" | ", parsedTxtCompress.Errors)}");
            AssertEqual(2, parsedTxtCompress.Options.InputPaths.Length);

            // 4. 默认 CommentFilePath="注释.txt" 不存在时，ViewModel 接线不可把不存在的注释路径传给 --comment 导致退出码 2。
            var root = FindRepositoryRoot();
            var execText = File.ReadAllText(Path.Combine(root, "ViewModels", "MainWindowViewModel.Execution.cs"));
            Assert(execText.Contains("EnableComment && File.Exists(CommentFilePath)", StringComparison.Ordinal),
                "CopyCliCommandAsync 必须与 BuildBatchOperationOptions 一致校验注释文件存在性，避免生成无效 --comment");

            var axaml = File.ReadAllText(Path.Combine(root, "Views", "MainWindow.axaml"));
            Assert(axaml.Contains("CopyCliCommandCommand", StringComparison.Ordinal), "MainWindow.axaml 缺少 CopyCliCommandCommand");
            Assert(axaml.Contains("L.CopyCliCommand", StringComparison.Ordinal), "MainWindow.axaml 缺少 L.CopyCliCommand");
            Assert(axaml.Contains("L.TooltipCopyCliCommand", StringComparison.Ordinal), "MainWindow.axaml 缺少 L.TooltipCopyCliCommand");
            Assert(axaml.Contains("L.TooltipExistingFiles", StringComparison.Ordinal), "MainWindow.axaml 缺少 L.TooltipExistingFiles");
            Assert(axaml.Contains("L.AddAttachments", StringComparison.Ordinal) &&
                   axaml.Contains("TooltipAttachmentList", StringComparison.Ordinal),
                "MainWindow.axaml AddAttachments 复选框应绑定 TooltipAttachmentList");

            var aboutAxaml = File.ReadAllText(Path.Combine(root, "Views", "AboutWindow.axaml"));
            Assert(aboutAxaml.Contains("TooltipExportDiagnostics", StringComparison.Ordinal),
                "AboutWindow.axaml ExportDiagnostics 按钮应绑定 TooltipExportDiagnostics");

            // 批次 47：解压校验统一收敛至 BatchPreflightService.InspectDecompressionPath。
            var preflightCs = File.ReadAllText(Path.Combine(root, "Core", "Services", "BatchPreflightService.cs"));
            var guiPreflightCs = File.ReadAllText(Path.Combine(root, "Core", "Services", "GuiPreflightService.cs"));
            var adapterCs = File.ReadAllText(Path.Combine(root, "Core", "Services", "Pipeline", "DecompressionItemAdapter.cs"));
            var runnerCs = File.ReadAllText(Path.Combine(root, "HeadlessBatchRunner.cs"));
            Assert(preflightCs.Contains("InspectDecompressionPath", StringComparison.Ordinal),
                "BatchPreflightService 必须暴露统一解压预检方法 InspectDecompressionPath");
            Assert(guiPreflightCs.Contains("InspectDecompressionPath", StringComparison.Ordinal),
                "GuiPreflightService 应复用 BatchPreflightService.InspectDecompressionPath");
            Assert(adapterCs.Contains("InspectDecompressionPath", StringComparison.Ordinal),
                "DecompressionItemAdapter 应复用 BatchPreflightService.InspectDecompressionPath");
            Assert(runnerCs.Contains("InspectDecompressionPath", StringComparison.Ordinal),
                "HeadlessBatchRunner --dry-run 应复用 BatchPreflightService.InspectDecompressionPath");
        }
        finally
        {
            if (Directory.Exists(work))
            {
                Directory.Delete(work, recursive: true);
            }
        }

        return Task.CompletedTask;
    }

    // Gemini 4 Hydrogen, 2026-11-27：批次 44——GUI 无副作用预检试运行与自定义密码掩码接线守护。
    private static Task TestGuiPreflightServiceAndPasswordMasking()
    {
        var work = Path.Combine(Path.GetTempPath(), "bc-gui-preflight-" + Guid.NewGuid().ToString("N"));
        var srcDir = Path.Combine(work, "src");
        var nonExistentOut = Path.Combine(work, "never-created-out");
        Directory.CreateDirectory(srcDir);

        try
        {
            var readyFile = Path.Combine(srcDir, "ready.txt");
            var skippedProcessed = Path.Combine(srcDir, "done【已压缩】.txt");
            var missingFile = Path.Combine(srcDir, "missing.txt");
            var subA = Path.Combine(srcDir, "subA");
            var subB = Path.Combine(srcDir, "subB");
            Directory.CreateDirectory(subA);
            Directory.CreateDirectory(subB);
            var collisionFileA = Path.Combine(subA, "same.txt");
            var collisionFileB = Path.Combine(subB, "same.txt");
            File.WriteAllText(readyFile, "ok");
            File.WriteAllText(skippedProcessed, "done");
            File.WriteAllText(collisionFileA, "collideA");
            File.WriteAllText(collisionFileB, "collideB");

            // 1. 压缩预检：不得创建输出目录，并准确报告就绪/跳过/缺失与碰撞/选项冲突/不支持格式问题。
            var compBlocked = GuiPreflightService.InspectCompression(
                [readyFile, skippedProcessed, missingFile, collisionFileA, collisionFileB],
                new BatchOperationOptions
                {
                    OutputPath = nonExistentOut,
                    Extension = "rar",
                    SkipAlreadyProcessed = true,
                    MaxConcurrency = 2
                },
                recommendedConcurrency: 4,
                hasUpdateLockConflict: true);
            Assert(!compBlocked.CanExecute, "存在输出碰撞或更新/锁定冲突时 CanExecute 必须为 false");
            Assert(!Directory.Exists(nonExistentOut), "预检绝不可创建输出目录");
            AssertEqual(1, compBlocked.ReadyCount);
            AssertEqual(1, compBlocked.SkippedCount);
            AssertEqual(1, compBlocked.MissingCount);
            Assert(compBlocked.IssueCount >= 2, $"应检出冲突，实际 IssueCount={compBlocked.IssueCount}");

            // 不支持的压缩格式（如 iso）也必须给出明确阻断提示。
            var isoBlocked = GuiPreflightService.InspectCompression(
                [readyFile],
                new BatchOperationOptions { OutputPath = nonExistentOut, Extension = "iso" },
                recommendedConcurrency: 4,
                hasUpdateLockConflict: false);
            Assert(!isoBlocked.CanExecute, "iso 格式不可创建，预检必须拦截");
            Assert(!Directory.Exists(nonExistentOut), "格式阻断预检也绝不可创建输出目录");

            // 正常场景可通过预检，且仍不创建输出目录。
            var compPass = GuiPreflightService.InspectCompression(
                [readyFile],
                new BatchOperationOptions { OutputPath = nonExistentOut, Extension = "7z" },
                recommendedConcurrency: 4,
                hasUpdateLockConflict: false);
            Assert(compPass.CanExecute, "正常单一文件应通过预检");
            AssertEqual(1, compPass.ReadyCount);
            AssertEqual(0, compPass.IssueCount);
            Assert(!Directory.Exists(nonExistentOut), "通过的预检也绝不可创建输出目录");

            // 2. 解压预检：校验缺失项、重复折叠、跳过已解压与空输出目录警告。
            var archive1 = Path.Combine(srcDir, "pkg.7z");
            var archiveProcessed = Path.Combine(srcDir, "done【已解压】.7z");
            File.WriteAllText(archive1, "7z-stub");
            File.WriteAllText(archiveProcessed, "done-stub");

            var decPass = GuiPreflightService.InspectDecompression(
                [
                    new FileEntry { FilePath = archive1 },
                    new FileEntry { FilePath = archive1 }, // 重复折叠跳过
                    new FileEntry { FilePath = archiveProcessed }, // 【已解压】跳过
                    new FileEntry { FilePath = Path.Combine(srcDir, "not-here.7z") }
                ],
                new BatchOperationOptions { OutputPath = nonExistentOut, Extension = "7z", SkipAlreadyProcessed = true },
                recommendedConcurrency: 4);
            Assert(decPass.CanExecute, "有至少 1 个合法归档且输出目录非空时解压预检应可执行");
            AssertEqual(1, decPass.ReadyCount);
            AssertEqual(2, decPass.SkippedCount);
            AssertEqual(1, decPass.MissingCount);
            Assert(!Directory.Exists(nonExistentOut), "解压预检也绝不可创建输出目录");

            // 3. 界面接线与自定义密码掩码守护。
            var root = FindRepositoryRoot();
            var axaml = File.ReadAllText(Path.Combine(root, "Views", "MainWindow.axaml"));
            Assert(axaml.Contains("PreflightPreviewCommand", StringComparison.Ordinal),
                "MainWindow.axaml 缺少 PreflightPreviewCommand 按钮");
            Assert(axaml.Contains("PasswordChar=\"●\"", StringComparison.Ordinal),
                "CustomPassword 输入框缺少 PasswordChar 掩码保护");
            Assert(axaml.Contains("RevealPassword=\"{Binding ShowCustomPassword}\"", StringComparison.Ordinal),
                "CustomPassword 输入框未绑定 RevealPassword");
            Assert(axaml.Contains("L.ShowPassword", StringComparison.Ordinal),
                "MainWindow.axaml 缺少显示密码切换按钮");

            var codeBehind = File.ReadAllText(Path.Combine(root, "Views", "MainWindow.axaml.cs"));
            Assert(codeBehind.Contains("ShowPreflightDialogRequested = ShowScrollableTextDialogAsync", StringComparison.Ordinal),
                "MainWindow.axaml.cs 未接线 ShowPreflightDialogRequested");
        }
        finally
        {
            if (Directory.Exists(work))
            {
                Directory.Delete(work, recursive: true);
            }
        }

        return Task.CompletedTask;
    }
}
