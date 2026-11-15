using BatchCompress.Avalonia.ViewModels;

// WorkBuddy, 2026-11-16：批次 22（P1）——界面日志有界缓冲。
// 与 Program.cs 保持一致：测试工程使用全局命名空间。
internal static partial class Program
{
    private static Task TestLogBuffer()
    {
        // 1. 上限裁剪：6000 行只保留最新 5000 行，并在顶部标注省略数量。
        var buffer = new LogBuffer(maxLines: 5_000, flushIntervalMs: 200);
        for (var i = 0; i < 6_000; i++)
        {
            buffer.Append($"line-{i}\n");
        }

        var text = buffer.Flush();
        AssertEqual(1_000, buffer.OmittedCount);
        Assert(text.Contains("已省略最早的 1000 行", StringComparison.Ordinal), "缺少省略提示");
        Assert(text.Contains("line-5999", StringComparison.Ordinal), "最新一行必须保留");
        Assert(!text.Contains("line-999\n", StringComparison.Ordinal), "最旧的行必须被丢弃");

        // 2. 多行与三种换行：\r\n、\n、\r 都按行拆分，末尾空段不产生空行。
        var multiline = new LogBuffer(maxLines: 100, flushIntervalMs: 0);
        multiline.Append("a\r\nb\nc\rd\n");
        var multiText = multiline.Flush();
        AssertEqual(4, multiText.Split('\n', StringSplitOptions.RemoveEmptyEntries).Length);

        // 3. 节流：第一次立即通过，紧接着第二次未到间隔必须返回 false。
        var throttled = new LogBuffer(maxLines: 100, flushIntervalMs: 60_000);
        throttled.Append("first");
        Assert(throttled.TryFlush(out var first) && first.Contains("first", StringComparison.Ordinal),
            "首次刷新应立即通过");
        throttled.Append("second");
        Assert(!throttled.TryFlush(out _), "间隔内不应再次刷新");
        Assert(throttled.IsDirty, "被节流的刷新必须保留待刷新标记");

        // 4. 强制 Flush 不受节流限制，且清掉待刷新标记。
        var forced = throttled.Flush();
        Assert(forced.Contains("second", StringComparison.Ordinal), "强制刷新必须包含新内容");
        Assert(!throttled.IsDirty, "强制刷新后不应再标记待刷新");

        // 5. 清空：内容、省略计数与节流状态一起归零。
        throttled.Clear();
        AssertEqual(0, throttled.OmittedCount);
        AssertEqual(string.Empty, throttled.Flush());

        // 6. 空文本与 null 不产生空行，也不触发待刷新。
        var empty = new LogBuffer();
        empty.Append(null);
        empty.Append(string.Empty);
        Assert(!empty.IsDirty, "空内容不应触发刷新");

        // 7. 量级验证：连续追加 10 万行，最终文本仍是有界的 5000 行，且耗时不失控。
        var watch = System.Diagnostics.Stopwatch.StartNew();
        var bulk = new LogBuffer(maxLines: 5_000, flushIntervalMs: 0);
        for (var i = 0; i < 100_000; i++)
        {
            bulk.Append($"row-{i}\n");
        }

        var bulkText = bulk.Flush();
        watch.Stop();
        var bulkLines = bulkText.Split('\n', StringSplitOptions.RemoveEmptyEntries);
        // 第一行是省略提示，其余是保留的 5000 行。
        AssertEqual(5_001, bulkLines.Length);
        AssertEqual(95_000, bulk.OmittedCount);
        Assert(watch.ElapsedMilliseconds < 5_000,
            $"10 万行追加耗时过长：{watch.ElapsedMilliseconds} ms");

        return Task.CompletedTask;
    }
}
