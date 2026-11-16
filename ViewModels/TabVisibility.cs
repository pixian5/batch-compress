namespace BatchCompress.Avalonia.ViewModels;

// WorkBuddy, 2026-11-16：0.8.13——把「开始页两个操作按钮何时可见」抽成不依赖界面框架的纯逻辑，
// 既方便单元测试，也避免属性通知漏项只能靠人工点界面发现。
public static class TabVisibility
{
    /// <summary>顶部页签：0=压缩配置，1=解压配置，2=开始，3=成功记录，4=失败记录，5=命令日志。</summary>
    public static bool IsCompressionActionVisible(int activeTab) => activeTab is 0 or 2;

    public static bool IsDecompressionActionVisible(int activeTab) => activeTab is 1 or 2;
}
