using System;
using System.Threading.Tasks;
using BatchCompress.Avalonia.Core.Services;

// WorkBuddy, 2026-11-16：0.8.13——诊断包脱敏的引号边界回归测试。
// 归档程序在密码含空格时会把整个参数加引号，写成 "-pSuper Secret 123"，
// 引号落在 -p 前面，旧的「-p 前必须是行首或空白」规则全部落空。
internal static partial class Program
{
    private static Task TestDiagnosticsRedactsQuotedWholeArgumentPasswords()
    {
        const string plainSecret = "SuperSecret123";
        const string spacedSecret = "Super Secret 123";
        const string headerSecret = "Head Secret 456";
        const string assignmentSecret = "Equals Secret 789";
        const string longOptionSecret = "Long Option Secret";

        var input =
            $"7zz a -p{plainSecret} out.7z\n" +
            $"7zz a \"-p{spacedSecret}\" out.7z\n" +
            $"rar a \"-hp{headerSecret}\" out.rar\n" +
            $"app compress \"--password {longOptionSecret}\" -e 7z\n" +
            $"app compress \"--password={assignmentSecret}\" -e 7z\n" +
            $"app compress --password={plainSecret} -e 7z\n";

        var output = DiagnosticsExportService.RedactSensitiveText(input);

        foreach (var secret in new[] { plainSecret, spacedSecret, headerSecret, assignmentSecret, longOptionSecret })
        {
            Assert(!output.Contains(secret, StringComparison.Ordinal),
                $"引号包住整个参数时密码仍残留：{secret}");
        }

        Assert(output.Contains("\"-p***\"", StringComparison.Ordinal),
            "引号形式的 -p 应替换为 \"-p***\"");
        Assert(output.Contains("\"-hp***\"", StringComparison.Ordinal),
            "引号形式的 -hp 应替换为 \"-hp***\"");
        Assert(output.Contains("\"--password ***\"", StringComparison.Ordinal),
            "引号形式的 --password 应替换为占位符");
        Assert(output.Contains("\"--password=***\"", StringComparison.Ordinal),
            "引号形式的 --password= 应替换为占位符");

        // 幂等：已经脱敏的文本再处理一次不应改变结果。
        var secondPass = DiagnosticsExportService.RedactSensitiveText(output);
        AssertEqual(output, secondPass);

        return Task.CompletedTask;
    }

    private static Task TestDiagnosticsRedactionLeavesOrdinaryPathsAlone()
    {
        // 普通路径里出现 -p / -hp 字样时不能被误伤。
        const string input =
            "7zz a out.7z \"/tmp/-pictures/photo.jpg\"\n" +
            "7zz a out.7z /tmp/-pictures/photo.jpg\n" +
            "7zz a out.7z \"C:\\Tools\\-hp-data\\file.txt\"\n" +
            "7zz a -pSecret out.7z\n";

        var output = DiagnosticsExportService.RedactSensitiveText(input);

        Assert(output.Contains("\"/tmp/-pictures/photo.jpg\"", StringComparison.Ordinal),
            "绝对路径中的 -pictures 不应被当成密码开关");
        Assert(output.Contains("/tmp/-pictures/photo.jpg", StringComparison.Ordinal),
            "未加引号的绝对路径同样不应被误伤");
        Assert(output.Contains("\"C:\\Tools\\-hp-data\\file.txt\"", StringComparison.Ordinal),
            "Windows 路径中的 -hp-data 不应被误伤");
        Assert(!output.Contains("Secret", StringComparison.Ordinal),
            "真正的 -p 密码仍必须脱敏");

        return Task.CompletedTask;
    }
}
