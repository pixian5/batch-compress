using System;
using System.IO;
using System.Threading.Tasks;
using BatchCompress.Avalonia;
using BatchCompress.Avalonia.Localization;

// WorkBuddy, 2026-11-16：0.8.13——命令行行为回归：
// 1) 显式 --lang 标记必须保留到图形界面初始化，不能被系统语言检测覆盖；
// 2) 来源缺失改为逐项计数后，解析层不能因为路径不存在而提前失败（解析本身不查存在性）。
internal static partial class Program
{
    private static Task TestExplicitLanguageFlagSurvivesToGui()
    {
        var service = LocalizationService.Instance;
        var originalLanguage = service.CurrentLanguage;
        try
        {
            var withLanguage = CommandLineHandler.ParseArguments(
                ["compress", "--lang", "de", "-i", "/tmp/a", "-o", "/tmp/b"]);
            Assert(withLanguage.Success, "带 --lang 的参数应解析成功");
            Assert(withLanguage.LanguageExplicitlySet, "解析结果必须携带显式语言标记");
            Assert(service.LanguageExplicitlySet, "显式 --lang 必须置位 LanguageExplicitlySet");
            AssertEqual("de", service.CurrentLanguage);

            // 下一次解析没有 --lang 时必须清除标记，避免静态状态残留。
            var withoutLanguage = CommandLineHandler.ParseArguments(
                ["compress", "-i", "/tmp/a", "-o", "/tmp/b"]);
            Assert(withoutLanguage.Success, "不带 --lang 的参数应解析成功");
            Assert(!withoutLanguage.LanguageExplicitlySet, "未指定 --lang 时解析结果不得置位显式语言标记");
            Assert(!service.LanguageExplicitlySet, "未指定 --lang 时不得保留上一次的显式标记");

            // 界面构造函数必须读取该标记；否则系统语言检测会覆盖命令行指定的语言。
            var viewModel = File.ReadAllText(
                Path.Combine(FindRepositoryRoot(), "ViewModels", "MainWindowViewModel.cs"));
            Assert(viewModel.Contains("LanguageExplicitlySet", StringComparison.Ordinal),
                "界面初始化未检查显式语言标记");
        }
        finally
        {
            service.CurrentLanguage = originalLanguage;
            service.LanguageExplicitlySet = false;
        }

        return Task.CompletedTask;
    }
}
