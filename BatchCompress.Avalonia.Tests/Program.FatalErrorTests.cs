using BatchCompress.Avalonia.Core.Services;

// WorkBuddy, 2026-11-15：批次 19（P4-1）——归档致命输出解析。
// 与 Program.cs 保持一致：测试工程使用全局命名空间。
internal static partial class Program
{
    private static Task TestFatalOutputParsing()
    {
        // 实测 7-Zip 的 CRC（循环冗余校验）失败写的是标准错误里的这一行。
        var fatalCases = new (string Line, string Kind)[]
        {
            ("ERROR: CRC Failed : big.bin", "CRC 校验失败"),
            ("ERROR: Data Error : big.bin", "数据错误"),
            ("Unexpected end of archive", "归档意外结束"),
            ("Can not open file as archive", "无法作为归档打开"),
            ("Cannot find volume arc.7z.002", "分卷缺失")
        };

        foreach (var (line, kind) in fatalCases)
        {
            var error = ArchiveSubProgressParser.TryParseFatalError(line);
            Assert(error != null, $"应识别为致命错误：{line}");
            AssertEqual(kind, error!.Kind);
            AssertEqual(line, error.SourceLine);
        }

        // 保守策略：进度行、汇总行、普通警告、乃至密码错误都不能提前终止。
        // 密码错误时 7-Zip 仍可能继续处理其它条目，误杀会造成整批中断。
        var benign = new[]
        {
            "", "   ", "5%", "Extracting archive: arc.7z.001",
            "Sub items Errors: 1", "Everything is Ok", "WARNING: minor issue",
            "Path = /tmp/arc.7z.001", "ERROR: Wrong password : big.bin"
        };

        foreach (var line in benign)
        {
            Assert(ArchiveSubProgressParser.TryParseFatalError(line) is null,
                $"不能误判为致命错误：{line}");
        }

        var detail = ArchiveSubProgressParser.TryParseFatalError("ERROR: CRC Failed : big.bin")!;
        var exception = new ArchiveFatalOutputException(detail);
        Assert(exception.Error == detail, "异常必须携带结构化错误信息");
        Assert(exception.Message.Contains("CRC 校验失败", StringComparison.Ordinal) &&
               exception.Message.Contains("big.bin", StringComparison.Ordinal),
            "异常消息必须带分类与原始输出行，便于日志精准定位");

        return Task.CompletedTask;
    }
}
