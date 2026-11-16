using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Text.RegularExpressions;

namespace BatchCompress.Avalonia.Localization;

/// <summary>
/// 管理当前语言并提供本地化字符串。
/// </summary>
// GPT-5, 2026-08-05：单例语言协调器。它发布完整的 LanguageStrings 实例，并向主窗口选择器提供支持的语言标签。
public class LocalizationService : INotifyPropertyChanged
{
    private static LocalizationService? _instance;
    public static LocalizationService Instance => _instance ??= new LocalizationService();
    
    private LanguageStrings _strings;
    private string _currentLanguage = "zh-CN";

    private static string WithVersion(string title)
    {
        // 资源文件只存基础标题；这里先剥掉可能残留的版本后缀，保证重复调用幂等。
        var baseTitle = Regex.Replace(title, @"\s+v\d+(?:\.\d+)*$", string.Empty);
        var version = typeof(LocalizationService).Assembly
            .GetCustomAttribute<AssemblyInformationalVersionAttribute>()?
            .InformationalVersion?.Split('+')[0];
        return string.IsNullOrWhiteSpace(version) ? baseTitle : $"{baseTitle} v{version}";
    }

    // WorkBuddy, 2026-11-16：批次 24（P2）——词条改为从嵌入的 JSON 资源加载，
    // 原来五份 Create*() 内置字典已移除；资源缺失时回退 LanguageStrings 的默认值。
    private static LanguageStrings LoadLanguage(string language)
    {
        var strings = LocalizationLoader.Load(language);
        strings.WindowTitle = WithVersion(strings.WindowTitle);
        return strings;
    }
    
    public event PropertyChangedEventHandler? PropertyChanged;
    
    private LocalizationService()
    {
        _strings = LoadLanguage(_currentLanguage);
    }
    
    protected void OnPropertyChanged([CallerMemberName] string? propertyName = null)
    {
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
    }
    
    /// <summary>
    /// 对外通知属性改变。
    /// </summary>
    public void NotifyPropertyChanged(string? propertyName = null)
    {
        OnPropertyChanged(propertyName);
    }
    
    /// <summary>
    /// 带显示名称的可用语言。
    /// </summary>
    public static Dictionary<string, string> AvailableLanguages { get; } = new()
    {
        { "zh-CN", "简体中文" },
        { "zh-TW", "繁體中文" },
        { "en", "English" },
        { "ja", "日本語" },
        { "de", "Deutsch" }
    };
    
    /// <summary>
    /// 获取或设置当前语言代码。
    /// </summary>
    public string CurrentLanguage
    {
        get => _currentLanguage;
        set
        {
            // GPT-5, 2026-08-05：拒绝未知语言代码，避免绑定观察到未完整初始化的语言对象。
            if (_currentLanguage != value && AvailableLanguages.ContainsKey(value))
            {
                _currentLanguage = value;
                // WorkBuddy, 2026-11-16：批次 24——统一走资源加载，未知语言回退简体中文。
                _strings = LoadLanguage(value);
                OnPropertyChanged();
                OnPropertyChanged(nameof(Strings));
                _strings.RaiseAllPropertiesChanged();
            }
        }
    }
    
    /// <summary>
    /// 命令行是否显式指定了 --lang。图形界面启动时据此跳过系统语言检测，
    /// 否则「单独使用 --lang 直接以该语言启动界面」会被系统语言覆盖。
    /// </summary>
    public bool LanguageExplicitlySet { get; set; }

    /// <summary>
    /// 获取当前本地化字符串集合。
    /// </summary>
    public LanguageStrings Strings
    {
        get => _strings;
        private set
        {
            _strings = value;
            OnPropertyChanged();
        }
    }
    
}
