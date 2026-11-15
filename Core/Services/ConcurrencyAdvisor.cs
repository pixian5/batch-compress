using System;

namespace BatchCompress.Avalonia.Core.Services;

/// <summary>
/// 硬件感知的并发度推荐结果。只携带数值与诊断信息，展示文案由界面层按当前语言生成。
/// </summary>
/// <param name="Recommended">最终推荐的并发任务数，恒在 1-16 之间。</param>
/// <param name="CoreCount">检测到的逻辑处理器数量。</param>
/// <param name="AvailableMemoryBytes">检测到的可用内存字节数。</param>
/// <param name="CoreLimit">按处理器数量算出的上限。</param>
/// <param name="MemoryLimit">按可用内存算出的上限。</param>
public sealed record ConcurrencyRecommendation(
    int Recommended,
    int CoreCount,
    long AvailableMemoryBytes,
    int CoreLimit,
    int MemoryLimit);

/// <summary>
/// 依据处理器数量与可用内存，为批量归档任务推荐一个安全的并发度。
/// Antigravity/WorkBuddy, 2026-11-15：批次 18（P4-2）——低内存设备上同时跑多个
/// 7zz 极限压缩任务容易触发内存耗尽（OOM）或被系统强杀，因此默认推荐值取
/// 「处理器上限」与「内存上限」中较小的一个，并钳制在既有 1-16 区间内。
/// </summary>
public static class ConcurrencyAdvisor
{
    /// <summary>既有命令行与界面共同约定的并发度下限。</summary>
    public const int MinConcurrency = 1;

    /// <summary>既有命令行与界面共同约定的并发度上限。</summary>
    public const int MaxConcurrency = 16;

    /// <summary>
    /// 单个归档子进程的保守内存预算。7z 在高压缩级别下字典较大，
    /// 512 MB 留出余量；宁可少并发，也不要让批处理中途被系统杀掉。
    /// </summary>
    public const long PerTaskMemoryBytes = 512L * 1024 * 1024;

    /// <summary>
    /// 计算推荐并发度。两个可注入参数仅供单元测试构造确定场景，
    /// 生产路径一律读取真实硬件信息。
    /// </summary>
    public static ConcurrencyRecommendation Recommend(int? coreCount = null, long? availableMemoryBytes = null)
    {
        var cores = coreCount ?? GetCoreCount();
        var memory = availableMemoryBytes ?? GetAvailableMemoryBytes();

        // 处理器上限：留一半核心给界面渲染、磁盘 IO 与系统本身，避免压缩把机器卡死。
        var coreLimit = Math.Max(MinConcurrency, cores / 2);

        // 内存上限：按每个归档进程 512 MB 的保守预算均分可用内存。
        var memoryLimit = memory <= 0
            ? MinConcurrency
            : Math.Max(MinConcurrency, (int)Math.Min(int.MaxValue, memory / PerTaskMemoryBytes));

        var recommended = Math.Clamp(Math.Min(coreLimit, memoryLimit), MinConcurrency, MaxConcurrency);
        return new ConcurrencyRecommendation(recommended, cores, memory, coreLimit, memoryLimit);
    }

    /// <summary>读取逻辑处理器数，异常或异常值时保守返回 1。</summary>
    private static int GetCoreCount()
    {
        try
        {
            return Math.Max(MinConcurrency, Environment.ProcessorCount);
        }
        catch (Exception)
        {
            return MinConcurrency;
        }
    }

    /// <summary>
    /// 读取可用内存。使用 GC（垃圾回收器）的 GCMemoryInfo，.NET 10 在
    /// Windows、macOS、Linux 上均可用，且会尊重容器/cgroup 的内存限制，
    /// 不需要调用平台专属命令，也不会把本机判断硬编码到服务器环境。
    /// 取不到有效值时不猜，直接回退到最保守的 1。
    /// </summary>
    private static long GetAvailableMemoryBytes()
    {
        try
        {
            var info = GC.GetGCMemoryInfo();
            return info.TotalAvailableMemoryBytes > 0 ? info.TotalAvailableMemoryBytes : 0;
        }
        catch (Exception)
        {
            return 0;
        }
    }

    /// <summary>把字节数格式化为便于日志阅读的 GB 数值。</summary>
    public static double ToGigabytes(long bytes) => bytes / (1024.0 * 1024.0 * 1024.0);
}
