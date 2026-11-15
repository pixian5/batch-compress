using BatchCompress.Avalonia.Core.Models;
using BatchCompress.Avalonia.Core.Services;

// WorkBuddy, 2026-11-15：批次 20（P4-3）——批处理结果报告服务。
// 与 Program.cs 保持一致：测试工程使用全局命名空间。
internal static partial class Program
{
    private static Task TestBatchReportService()
    {
        var collector = new BatchReportCollector();
        collector.SetOperation(true);

        // 成功项：计数增量触发一条记录。
        collector.Report(new OperationProgressInfo
        {
            CurrentFile = "a.txt",
            CurrentSourcePath = "/tmp/a.txt",
            Message = "成功: a.txt",
            SuccessCount = 1,
            ProcessedSizeGB = 0.01,
            Elapsed = TimeSpan.FromSeconds(1.25)
        });
        AssertEqual(1, collector.Entries.Count);
        AssertEqual("success", collector.Entries[0].Status);
        AssertEqual("a.txt", collector.Entries[0].Source);
        AssertEqual("compress", collector.Entries[0].Operation);

        // 命令与进程原始输出整条排除：那是唯一可能带明文密码的地方。
        collector.Report(new OperationProgressInfo
        {
            IsCommandOutput = true,
            Message = "7zz a -pplaintext-secret a.7z a.txt",
            SuccessCount = 2
        });
        AssertEqual(1, collector.Entries.Count);

        // 失败项单独成行，带原因。
        collector.Report(new OperationProgressInfo
        {
            CurrentFile = "b.txt",
            Message = "失败: b.txt - CRC Failed",
            SuccessCount = 1,
            FailCount = 1,
            Elapsed = TimeSpan.FromSeconds(0.5)
        });
        AssertEqual(2, collector.Entries.Count);
        AssertEqual("failed", collector.Entries[1].Status);

        var csv = BatchReportWriter.ToCsv(collector.Entries);
        Assert(csv.Contains("序号", StringComparison.Ordinal) &&
               csv.Contains("a.txt", StringComparison.Ordinal) &&
               csv.Contains("b.txt", StringComparison.Ordinal),
            "CSV 必须含表头与逐项记录");
        Assert(!csv.Contains("plaintext-secret", StringComparison.Ordinal),
            "CSV 不得包含凭据文本");

        var markdown = BatchReportWriter.ToMarkdown(collector.Entries);
        Assert(markdown.Contains("# 批处理结果报告", StringComparison.Ordinal) &&
               markdown.Contains("| 序号 |", StringComparison.Ordinal),
            "Markdown 必须是带表头的汇总表");

        // 第二道防护：即便有人把命令文本塞进用户可见消息，也会被省略。
        var guarded = new BatchReportCollector();
        guarded.Report(new OperationProgressInfo
        {
            SuccessCount = 1,
            CurrentFile = "c.txt",
            Message = "7zz a -pplaintext-secret c.7z c.txt"
        });
        AssertEqual(BatchReportCollector.RedactedMarker, guarded.Entries[0].Message);

        // 按扩展名选格式，并自动创建父目录。
        var tempDir = Path.Combine(Path.GetTempPath(), "bc-report-" + Guid.NewGuid().ToString("N"));
        try
        {
            var csvPath = BatchReportWriter.Write(Path.Combine(tempDir, "sub", "r.csv"), collector.Entries);
            Assert(File.Exists(csvPath) && File.ReadAllText(csvPath).Contains("a.txt", StringComparison.Ordinal),
                "CSV 文件必须写入指定路径（含自动建目录）");

            var mdPath = BatchReportWriter.Write(Path.Combine(tempDir, "r.md"), collector.Entries);
            Assert(File.ReadAllText(mdPath).Contains("# 批处理结果报告", StringComparison.Ordinal),
                ".md 扩展名必须生成 Markdown");
        }
        finally
        {
            if (Directory.Exists(tempDir)) Directory.Delete(tempDir, recursive: true);
        }

        // 上限：超出 MaxEntries 的条目只累计计数，报告必须注明省略数量。
        var capped = new BatchReportCollector();
        capped.SetOperation(true);
        for (var i = 0; i < BatchReportCollector.MaxEntries + 1; i++)
        {
            capped.Report(new OperationProgressInfo
            {
                CurrentFile = $"item-{i}",
                SuccessCount = i + 1
            });
        }

        AssertEqual(BatchReportCollector.MaxEntries, capped.Entries.Count);
        AssertEqual(1, capped.OmittedEntries);

        var cappedMarkdown = BatchReportWriter.ToMarkdown(capped.Entries, capped.OmittedEntries);
        Assert(cappedMarkdown.Contains("另有 1 条因超出上限未列出", StringComparison.Ordinal),
            "Markdown 报告必须注明省略数量");
        var cappedCsv = BatchReportWriter.ToCsv(capped.Entries, capped.OmittedEntries);
        Assert(cappedCsv.Contains("另有 1 条因超出上限未列出", StringComparison.Ordinal),
            "CSV 报告必须注明省略数量");

        return Task.CompletedTask;
    }
}
