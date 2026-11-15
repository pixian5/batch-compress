using System.Reflection;
using BatchCompress.Avalonia.Core.Models;
using BatchCompress.Avalonia.Core.Services;
using BatchCompress.Avalonia.Localization;
using BatchCompress.Avalonia.ViewModels;

// WorkBuddy, 2026-11-16：审查修复的回归测试。
// 与 Program.cs 保持一致：测试工程使用全局命名空间。
internal static partial class Program
{
    /// <summary>报告文案跟随语言、CSV 公式注入防护、中性标记与脱敏文案翻译。</summary>
    private static Task TestReviewReportLocalizationAndSafety()
    {
        var originalLanguage = LocalizationService.Instance.CurrentLanguage;
        try
        {
            var collector = new BatchReportCollector();
            collector.SetOperation(false); // 解压
            collector.Report(new OperationProgressInfo
            {
                CurrentFile = "=1+1.txt",
                Message = "done",
                SuccessCount = 1
            });

            // 收集器只存语言无关标记，语言在写出时才决定。
            AssertEqual("success", collector.Entries[0].Status);
            AssertEqual("extract", collector.Entries[0].Operation);

            LocalizationService.Instance.CurrentLanguage = "zh-CN";
            var zhCsv = BatchReportWriter.ToCsv(collector.Entries);
            Assert(zhCsv.Contains("序号", StringComparison.Ordinal) &&
                   zhCsv.Contains("解压", StringComparison.Ordinal) &&
                   zhCsv.Contains("成功", StringComparison.Ordinal),
                "中文报告必须含中文表头、状态与操作名");
            Assert(zhCsv.Contains("\"'=1+1.txt\"", StringComparison.Ordinal),
                "以 = 开头的来源名必须被中和，不能被表格软件当公式");
            Assert(!zhCsv.Contains("\"=1+1.txt\"", StringComparison.Ordinal),
                "CSV 里不应残留未中和的公式文本");

            LocalizationService.Instance.CurrentLanguage = "en";
            var enCsv = BatchReportWriter.ToCsv(collector.Entries);
            Assert(enCsv.Contains("\"No.\"", StringComparison.Ordinal) &&
                   enCsv.Contains("\"Operation\"", StringComparison.Ordinal) &&
                   enCsv.Contains("\"Extract\"", StringComparison.Ordinal) &&
                   enCsv.Contains("\"Success\"", StringComparison.Ordinal),
                "英文报告必须使用英文表头、状态与操作名");
            Assert(!enCsv.Contains("序号", StringComparison.Ordinal) &&
                   !enCsv.Contains("解压", StringComparison.Ordinal),
                "英文报告不得残留中文骨架");
            var enMarkdown = BatchReportWriter.ToMarkdown(collector.Entries);
            Assert(enMarkdown.Contains("# Batch Results Report", StringComparison.Ordinal),
                "英文 Markdown 标题必须本地化");

            // 含凭据消息在写出时按当前语言翻译占位标记。
            var guarded = new BatchReportCollector();
            guarded.Report(new OperationProgressInfo
            {
                SuccessCount = 1,
                CurrentFile = "c.txt",
                Message = "7zz a -pplaintext-secret c.7z c.txt"
            });
            AssertEqual(BatchReportCollector.RedactedMarker, guarded.Entries[0].Message);
            Assert(BatchReportWriter.ToCsv(guarded.Entries)
                    .Contains("(text containing credentials omitted)", StringComparison.Ordinal),
                "英文报告里的脱敏说明必须是英文");

            LocalizationService.Instance.CurrentLanguage = "zh-CN";
            Assert(BatchReportWriter.ToCsv(guarded.Entries)
                    .Contains("（已省略含凭据的文本）", StringComparison.Ordinal),
                "中文报告里的脱敏说明必须是中文");

            return Task.CompletedTask;
        }
        finally
        {
            // 语言服务是单例，必须还原，避免污染后续测试。
            LocalizationService.Instance.CurrentLanguage = originalLanguage;
        }
    }

    /// <summary>致命异常强制落盘、抑制次数不丢、临时文件不残留、权限始终 0600。</summary>
    private static Task TestReviewCrashLogForceAndSuppression()
    {
        var dir = Path.Combine(Path.GetTempPath(), "bc-crash-review-" + Guid.NewGuid().ToString("N"));
        CrashLogService.DirectoryOverrideForTests = dir;
        CrashLogService.ResetStateForTests();

        try
        {
            Exception captured;
            try { throw new InvalidOperationException("重复故障"); }
            catch (Exception ex) { captured = ex; }

            CrashLogService.Write("review", captured);
            AssertEqual(1, Directory.GetFiles(dir, "batchcompress-crash_*.log").Length);

            // 2 秒内同签名连写三次，全部被抑制。
            for (var i = 0; i < 3; i++)
            {
                CrashLogService.Write("review", captured);
            }
            AssertEqual(1, Directory.GetFiles(dir, "batchcompress-crash_*.log").Length);

            // 强制写入必须绕过去重，并把抑制次数一起带出来。
            CrashLogService.Write("review", captured, "强制落盘", force: true);
            var files = Directory.GetFiles(dir, "batchcompress-crash_*.log");
            AssertEqual(2, files.Length);
            Assert(files.Any(file => File.ReadAllText(file).Contains("suppressed=3", StringComparison.Ordinal)),
                "强制写入必须落盘被抑制的重复次数");

            // 进程退出补写路径：再抑制两次，直接触发退出钩子，断言追加到最近的日志。
            CrashLogService.Write("review", captured);
            CrashLogService.Write("review", captured);
            var flush = typeof(CrashLogService).GetMethod(
                "FlushPendingSuppressed", BindingFlags.NonPublic | BindingFlags.Static);
            Assert(flush is not null, "必须存在进程退出补写入口");
            flush!.Invoke(null, null);
            Assert(files.Any(file => File.ReadAllText(file)
                    .Contains("suppressed=2 (flushed on process exit)", StringComparison.Ordinal)),
                "进程退出时必须补记被抑制的重复次数");

            if (!OperatingSystem.IsWindows())
            {
                foreach (var file in Directory.GetFiles(dir, "batchcompress-crash_*.log"))
                {
                    var mode = File.GetUnixFileMode(file);
                    Assert((mode & (UnixFileMode.GroupRead | UnixFileMode.OtherRead)) == 0,
                        $"崩溃日志必须保持 0600，实际 {mode}");
                }
            }

            AssertEqual(0, Directory.GetFiles(dir, "batchcompress-crash_*.tmp").Length);
            return Task.CompletedTask;
        }
        finally
        {
            CrashLogService.DirectoryOverrideForTests = null;
            CrashLogService.ResetStateForTests();
            try { if (Directory.Exists(dir)) Directory.Delete(dir, recursive: true); }
            catch { /* 清理失败不影响结论 */ }
        }
    }

    /// <summary>日志缓冲保留中间空行，只丢末尾换行产生的空段。</summary>
    private static Task TestReviewLogBufferBlankLines()
    {
        var trailing = new LogBuffer(100, 200);
        trailing.Append("行1\n\n行2\n");
        AssertEqual("行1\n\n行2\n", trailing.Flush());

        var noTrailing = new LogBuffer(100, 200);
        noTrailing.Append("a\n\nb");
        AssertEqual("a\n\nb\n", noTrailing.Flush());

        var multiple = new LogBuffer(100, 200);
        multiple.Append("a\n\n\nb\n");
        AssertEqual("a\n\n\nb\n", multiple.Flush());

        return Task.CompletedTask;
    }
}
