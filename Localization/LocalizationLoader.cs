using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text.Json;

namespace BatchCompress.Avalonia.Localization;

/// <summary>
/// 从嵌入的 JSON 资源加载界面词条。
/// WorkBuddy, 2026-11-16：批次 24（P2）——五种语言原来各写一个约 290 行的 Create*() 方法，
/// 现在改为 Localization/Resources/*.json；加一种语言只需加一份资源文件。
/// </summary>
public static class LocalizationLoader
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true
    };

    // 属性映射只建一次；键名大小写不敏感，允许 JSON 里写 WindowTitle / windowTitle。
    private static readonly Dictionary<string, PropertyInfo> WritableProperties = typeof(LanguageStrings)
        .GetProperties(BindingFlags.Public | BindingFlags.Instance)
        .Where(property => property.PropertyType == typeof(string) && property.CanWrite)
        .ToDictionary(property => property.Name, StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// 加载指定语言的词条。资源缺失或解析失败时返回 LanguageStrings 的内置默认值，
    /// 保证界面不会出现整片空白。
    /// </summary>
    public static LanguageStrings Load(string language)
    {
        var strings = new LanguageStrings();
        var document = ReadDocument(language);
        if (document?.Strings is null)
        {
            return strings;
        }

        foreach (var (key, value) in document.Strings)
        {
            if (!WritableProperties.TryGetValue(key, out var property))
            {
                Debug.WriteLine($"[Localization] {language} 含未知键：{key}");
                continue;
            }

            // 缺键时属性保持 LanguageStrings 的默认值，不会变成空串。
            property.SetValue(strings, value ?? string.Empty);
        }

        return strings;
    }

    /// <summary>该语言是否带嵌入资源文件。</summary>
    public static bool HasResource(string language) => FindResourceName(language) is not null;

    private static LocalizationDocument? ReadDocument(string language)
    {
        var resourceName = FindResourceName(language);
        if (resourceName is null)
        {
            Debug.WriteLine($"[Localization] 找不到 {language} 的资源文件，回退内置默认值");
            return null;
        }

        try
        {
            var assembly = typeof(LocalizationLoader).Assembly;
            using var stream = assembly.GetManifestResourceStream(resourceName);
            if (stream is null)
            {
                return null;
            }

            using var reader = new StreamReader(stream);
            return JsonSerializer.Deserialize<LocalizationDocument>(reader.ReadToEnd(), JsonOptions);
        }
        catch (Exception ex)
        {
            // 资源损坏不能让程序起不来，回退默认值并把原因写进调试输出。
            Debug.WriteLine($"[Localization] 读取 {language} 失败：{ex.Message}");
            return null;
        }
    }

    private static string? FindResourceName(string language) =>
        typeof(LocalizationLoader).Assembly.GetManifestResourceNames()
            .FirstOrDefault(name =>
                name.EndsWith($".Resources.{language}.json", StringComparison.OrdinalIgnoreCase));

    private sealed class LocalizationDocument
    {
        public string? Language { get; set; }
        public string? DisplayName { get; set; }
        public Dictionary<string, string>? Strings { get; set; }
    }
}
