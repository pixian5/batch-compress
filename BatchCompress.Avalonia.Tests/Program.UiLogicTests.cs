using System;
using System.IO;
using System.Threading.Tasks;
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
}
