using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using BatchCompress.Avalonia.Localization;

// WorkBuddy, 2026-11-16：批次 24（P2）——本地化资源一致性。
// 迁移到 JSON 后最容易出的问题是漏键/多键，必须先锁住集合再谈其它。
internal static partial class Program
{
    private static readonly string[] LocalizationLanguages = ["zh-CN", "zh-TW", "en", "ja", "de"];

    private static Task TestLocalizationResources()
    {
        var resourcesDirectory = Path.Combine(FindRepositoryRoot(), "Localization", "Resources");
        Assert(Directory.Exists(resourcesDirectory), "缺少 Localization/Resources 目录");

        var declaredProperties = typeof(LanguageStrings)
            .GetProperties()
            .Where(property => property.PropertyType == typeof(string))
            .Select(property => property.Name)
            .ToHashSet(StringComparer.Ordinal);

        Dictionary<string, HashSet<string>> keysByLanguage = new(StringComparer.Ordinal);
        foreach (var language in LocalizationLanguages)
        {
            var path = Path.Combine(resourcesDirectory, $"{language}.json");
            Assert(File.Exists(path), $"缺少 {language}.json");
            Assert(LocalizationLoader.HasResource(language), $"{language} 未嵌入到程序集");

            using var document = JsonDocument.Parse(File.ReadAllText(path));
            var strings = document.RootElement.GetProperty("strings");
            var keys = strings.EnumerateObject()
                .Select(property => property.Name)
                .ToHashSet(StringComparer.Ordinal);
            keysByLanguage[language] = keys;

            // 每个值都不能是空串，否则界面会出现空白文案。
            foreach (var property in strings.EnumerateObject())
            {
                Assert(!string.IsNullOrEmpty(property.Value.GetString()),
                    $"{language}.json 的 {property.Name} 为空");
            }

            // 资源里只存基础标题，版本由 LanguageStrings 使用方统一拼接。
            var title = strings.GetProperty("WindowTitle").GetString() ?? string.Empty;
            Assert(!title.Contains(" v1.0.0", StringComparison.Ordinal),
                $"{language}.json 的 WindowTitle 不应包含测试程序集版本");
        }

        // 五份资源的键集合必须完全一致，且与 LanguageStrings 的可写属性一致。
        var reference = keysByLanguage["zh-CN"];
        foreach (var (language, keys) in keysByLanguage)
        {
            Assert(reference.SetEquals(keys), $"{language}.json 的键集合与 zh-CN 不一致");
        }

        Assert(reference.SetEquals(declaredProperties),
            "资源键集合与 LanguageStrings 属性集合不一致");

        // 加载后每种语言的标题都应包含当前运行版本，且互不相同。
        var titles = new HashSet<string>(StringComparer.Ordinal);
        foreach (var language in LocalizationLanguages)
        {
            LocalizationService.Instance.CurrentLanguage = language;
            var strings = LocalizationService.Instance.Strings;
            Assert(strings.WindowTitle.Contains("v", StringComparison.Ordinal),
                $"{language} 的窗口标题缺少版本");
            titles.Add(strings.WindowTitle);
        }

        AssertEqual(LocalizationLanguages.Length, titles.Count);

        // 恢复默认语言，避免影响后续测试。
        LocalizationService.Instance.CurrentLanguage = "zh-CN";
        return Task.CompletedTask;
    }
}
