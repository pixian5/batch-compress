using System.Text.RegularExpressions;

namespace BatchCompress.Avalonia.Core.Services;

// WorkBuddy, 2026-11-17：0.8.16——诊断包、结果报告、NDJSON 三个对外出口
// 各自维护过一套密码判断；这里收成唯一实现，避免报告/JSON 的规则落后于诊断包。
public static class CredentialRedactor
{
    /// <summary>把 -p、-hp、--password 后面的值替换为 ***；无凭据时返回原文。</summary>
    public static string Redact(string? input)
    {
        if (string.IsNullOrEmpty(input))
        {
            return input ?? string.Empty;
        }

        // 密码含空格时，命令行展示会把整个参数加引号，写成 "-pSuper Secret 123"。
        // 先处理“引号包住整个参数”的三种形态；(?=\S) 避免把孤立的 "-p" 当成密码。
        var result = Regex.Replace(input, @"""(-hp|-p)(?=\S)([^""]*)""", @"""$1***""");

        // "--password=带空格" 与 "--password 带空格" 两种赋值/空格形态。
        result = Regex.Replace(result, @"""(--password)([=:])([^""]*)""", @"""$1$2***""");
        result = Regex.Replace(result, @"""(--password)\s+([^""]*)""", @"""$1 ***""");

        // --password "..." 与 --password ...（此时引号整体形态已处理过）。
        result = Regex.Replace(result, @"(--password\s+)""[^""]*""", "$1\"***\"");
        // 只吃到空白或引号为止，避免把已脱敏占位符后面的结尾引号一起吞掉。
        result = Regex.Replace(result, @"(--password\s+)(?!\*\*\*(?=$|[\s""]))[^\s""]+", "$1***");
        // 等号/冒号赋值形式；带引号与不带引号合并处理，避免二次替换。
        result = Regex.Replace(result, @"(--password)[=:](?:""[^""]*""|[^\s""]+)", "$1=***");

        // -p / -hp：必须位于行首或空白之后，避免误伤 --password 里的 -p。
        result = Regex.Replace(result, @"(^|\s)(-p)""[^""]*""", "$1$2\"***\"");
        result = Regex.Replace(result, @"(^|\s)(-p)(?!\*\*\*(?=$|[\s""]))[^\s""]+", "$1$2***");
        result = Regex.Replace(result, @"(^|\s)(-hp)""[^""]*""", "$1$2\"***\"");
        result = Regex.Replace(result, @"(^|\s)(-hp)(?!\*\*\*(?=$|[\s""]))[^\s""]+", "$1$2***");

        return result;
    }

    /// <summary>文本中是否包含可识别的凭据参数；用于报告选择整条省略。</summary>
    public static bool ContainsCredential(string? input)
    {
        if (string.IsNullOrEmpty(input))
        {
            return false;
        }

        return !string.Equals(Redact(input), input, System.StringComparison.Ordinal);
    }
}
