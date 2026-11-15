using System;
using System.IO;
using BatchCompress.Avalonia.Core.Services;

// WorkBuddy, 2026-11-16：批次 21（P0）——崩溃兜底日志。
// 与 Program.cs 保持一致：测试工程使用全局命名空间。
internal static partial class Program
{
    private static Task TestCrashLogService()
    {
        var dir = Path.Combine(Path.GetTempPath(), "bc-crash-" + Guid.NewGuid().ToString("N"));
        CrashLogService.DirectoryOverrideForTests = dir;
        CrashLogService.ResetStateForTests();

        try
        {
            // 1. 基本写入：阶段、版本、异常类型、消息、堆栈、上下文都要有。
            // 必须用真正抛出过的异常，否则 ToString() 里没有堆栈。
            Exception captured;
            try
            {
                throw new InvalidOperationException("界面渲染失败");
            }
            catch (Exception ex)
            {
                captured = ex;
            }

            CrashLogService.Write("unit-test", captured, "用例上下文");
            var files = Directory.GetFiles(dir, "batchcompress-crash_*.log");
            AssertEqual(1, files.Length);

            var text = File.ReadAllText(files[0]);
            Assert(text.Contains("stage=unit-test", StringComparison.Ordinal), "缺少阶段");
            Assert(text.Contains("version=", StringComparison.Ordinal), "缺少版本");
            Assert(text.Contains("InvalidOperationException", StringComparison.Ordinal), "缺少异常类型");
            Assert(text.Contains("界面渲染失败", StringComparison.Ordinal), "缺少异常消息");
            Assert(text.Contains("stack:", StringComparison.Ordinal) ||
                   text.Contains("   at ", StringComparison.Ordinal), "缺少堆栈");
            Assert(text.Contains("context=用例上下文", StringComparison.Ordinal), "缺少上下文");

            // 2. Unix 上必须是 0600（同组/其他用户不可读）。
            if (!OperatingSystem.IsWindows())
            {
                var mode = File.GetUnixFileMode(files[0]);
                Assert((mode & (UnixFileMode.GroupRead | UnixFileMode.OtherRead)) == 0,
                    $"崩溃日志必须是 0600，实际 {mode}");
            }

            // 3. 2 秒内同签名只写一条，防止异常风暴刷爆磁盘。
            CrashLogService.Write("unit-test", new InvalidOperationException("界面渲染失败"), "重复");
            AssertEqual(1, Directory.GetFiles(dir, "batchcompress-crash_*.log").Length);

            // 4. 不同阶段属于不同签名，应新写一条。
            CrashLogService.Write("unit-test-2", new InvalidOperationException("另一个错误"));
            AssertEqual(2, Directory.GetFiles(dir, "batchcompress-crash_*.log").Length);

            // 5. 非异常诊断也能写。
            CrashLogService.WriteMessage("unit-message", "配置损坏");
            AssertEqual(3, Directory.GetFiles(dir, "batchcompress-crash_*.log").Length);

            // 6. 异常对象自己的 ToString 抛异常时，兜底仍不能向外抛。
            CrashLogService.Write("unit-broken", new BrokenToStringException());
            AssertEqual(4, Directory.GetFiles(dir, "batchcompress-crash_*.log").Length);

            // 7. 轮转：写满 25 个旧文件后再写一条，总数不得超过 20。
            for (var i = 0; i < 25; i++)
            {
                var oldPath = Path.Combine(dir, $"batchcompress-crash_20200101_0000{i:00}_1.log");
                File.WriteAllText(oldPath, "old");
                File.SetLastWriteTimeUtc(oldPath, DateTime.UtcNow.AddMinutes(-i - 1));
            }

            CrashLogService.ResetStateForTests();
            CrashLogService.Write("unit-prune", new Exception("轮转"));
            var remaining = Directory.GetFiles(dir, "batchcompress-crash_*.log");
            Assert(remaining.Length <= 20, $"崩溃日志应最多保留 20 个，实际 {remaining.Length}");

            // 8. 重复注册全局兜底必须安全（只生效一次，不重复挂钩子）。
            CrashLogService.RegisterGlobalHandlers();
            CrashLogService.RegisterGlobalHandlers();
        }
        finally
        {
            CrashLogService.DirectoryOverrideForTests = null;
            CrashLogService.ResetStateForTests();
            try
            {
                if (Directory.Exists(dir)) Directory.Delete(dir, recursive: true);
            }
            catch
            {
                // 测试清理失败不影响结论。
            }
        }

        return Task.CompletedTask;
    }

    /// <summary>用于验证「异常自身的 ToString 也抛」时兜底不会二次崩溃。</summary>
    private sealed class BrokenToStringException : Exception
    {
        public override string ToString() => throw new InvalidOperationException("ToString 也坏了");
    }
}
