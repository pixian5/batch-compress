using BatchCompress.Avalonia.Core.Services;

// 与 Program.cs 保持一致：测试工程使用全局命名空间，分部类才能正确合并。
internal static partial class Program
{
    // WorkBuddy, 2026-11-15：批次 18（P4-2）——硬件感知并发推荐与 --concurrency auto 解析。

    private const long OneGiB = 1024L * 1024 * 1024;

    private static Task TestConcurrencyAdvisor()
    {
        // 八核 + 8 GiB：核心上限 4、内存上限 16，取较小者 4。
        var balanced = ConcurrencyAdvisor.Recommend(8, 8 * OneGiB);
        AssertEqual(4, balanced.Recommended);
        AssertEqual(4, balanced.CoreLimit);
        AssertEqual(16, balanced.MemoryLimit);

        // 双核 + 1 GiB：核心上限 1，低内存设备绝不能因为并发把机器拖垮。
        var small = ConcurrencyAdvisor.Recommend(2, OneGiB);
        AssertEqual(1, small.Recommended);
        AssertEqual(2, small.MemoryLimit);

        // 单份内存预算只允许一个任务时，即使核心多也回退到 1。
        var memoryBound = ConcurrencyAdvisor.Recommend(16, ConcurrencyAdvisor.PerTaskMemoryBytes);
        AssertEqual(1, memoryBound.Recommended);

        // 内存读不到时按最保守值处理，不能猜一个高数字。
        var unknownMemory = ConcurrencyAdvisor.Recommend(16, 0);
        AssertEqual(1, unknownMemory.Recommended);

        // 大服务器也不能越过既有 1-16 的界面/命令行上限。
        var server = ConcurrencyAdvisor.Recommend(128, 256 * OneGiB);
        AssertEqual(16, server.Recommended);

        // 真实硬件路径永远落在合法区间内。
        var actual = ConcurrencyAdvisor.Recommend();
        Assert(actual.Recommended >= ConcurrencyAdvisor.MinConcurrency &&
               actual.Recommended <= ConcurrencyAdvisor.MaxConcurrency,
            "生产路径推荐值必须落在 1-16");
        Assert(actual.CoreCount >= 1, "处理器数必须有效");
        AssertEqual(1.0, ConcurrencyAdvisor.ToGigabytes(OneGiB));

        return Task.CompletedTask;
    }

    private static Task TestAutoConcurrencyArgument()
    {
        var auto = BatchCompress.Avalonia.CommandLineHandler.ParseArguments(
            ["compress", "--input", "/tmp/a", "--output", "/tmp/o", "--concurrency", "auto"]);
        Assert(auto.Success, string.Join(" | ", auto.Errors));
        Assert(auto.Options.AutoConcurrency, "auto 必须开启硬件自适应");
        AssertEqual(1, auto.Options.MaxConcurrency);

        // 大小写不敏感，方便脚本里混用。
        var upper = BatchCompress.Avalonia.CommandLineHandler.ParseArguments(
            ["compress", "--input", "/tmp/a", "--output", "/tmp/o", "-j", "AUTO"]);
        Assert(upper.Success, string.Join(" | ", upper.Errors));
        Assert(upper.Options.AutoConcurrency, "AUTO 大写同样生效");

        // 显式数值仍然走原来的解析与范围校验，行为零漂移。
        var numeric = BatchCompress.Avalonia.CommandLineHandler.ParseArguments(
            ["compress", "--input", "/tmp/a", "--output", "/tmp/o", "-j", "3"]);
        Assert(numeric.Success, string.Join(" | ", numeric.Errors));
        Assert(!numeric.Options.AutoConcurrency, "显式数值不应开启 auto");
        AssertEqual(3, numeric.Options.MaxConcurrency);

        var invalid = BatchCompress.Avalonia.CommandLineHandler.ParseArguments(
            ["compress", "--input", "/tmp/a", "--output", "/tmp/o", "-j", "0"]);
        Assert(!invalid.Success, "并发度 0 仍必须被拒绝");

        // 五套帮助文本都要让使用者知道有 auto 这个取值。
        foreach (var language in new[] { "zh-CN", "zh-TW", "en", "ja", "de" })
        {
            var help = BatchCompress.Avalonia.CommandLineHandler.GetHelpText(language);
            Assert(help.Contains("auto", StringComparison.OrdinalIgnoreCase),
                $"{language} 帮助文本必须说明 auto 取值");
        }

        return Task.CompletedTask;
    }
}
