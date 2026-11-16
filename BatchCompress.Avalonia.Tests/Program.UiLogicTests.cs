using System;
using System.IO;
using System.Threading.Tasks;
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

        return Task.CompletedTask;
    }
}
