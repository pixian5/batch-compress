using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Input;
using Avalonia.Controls;
using Avalonia.Platform.Storage;
using Avalonia.Threading;
using BatchCompress.Avalonia.Core.Interfaces;
using BatchCompress.Avalonia.Core.Models;
using BatchCompress.Avalonia.Core.Services;
using BatchCompress.Avalonia.Localization;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace BatchCompress.Avalonia.ViewModels;

// GPT-5, 2026-08-05：持有主窗口全部可绑定状态，并将 UI 选择转换为 BatchOperationOptions。
// 仅界面相关的操作通过回调注入，使本类保持可测试且不依赖 Avalonia 控件。
public partial class MainWindowViewModel : ViewModelBase
{
    private readonly IArchiveEngine _archiveEngine;
    private readonly ISystemIntegration _systemIntegration;

    // WorkBuddy, 2026-11-15：批次 20（P4-3）——最近一次批处理的逐项结果收集器。
    internal BatchReportCollector? LastReportCollector;
    // Gemini 4 Neon, 2026-11-27：批次 38——最近一次批处理的失败项重试列表与模式上下文。
    internal FailedItemRetryList? LastFailedRetryList;
    private bool _lastOperationWasCompression = true;
    private bool _lastOperationWasPasswordBook;
    private string _lastOperationOriginalSourceList = string.Empty;
    private readonly BatchOperationService _batchOperationService;
    // GPT-5, 2026-09-30：GUI 的文件日志器（设计器模式下为 null），把服务层诊断写入用户数据目录 logs。
    private readonly FileLoggerService? _fileLogger;
    // GLM-5.3, 2026-11-04：批次 9——附件配置服务（构造可注入，测试用显式临时路径隔离用户目录）。
    private readonly EnclosureConfigService _enclosureConfig;
    // Gemini 4 Neon, 2026-11-27：批次 40（v0.8.29）——安全 GUI 用户偏好持久化服务。
    private readonly UserPreferencesService _preferencesService;
    private bool _isApplyingInitialLanguage;
    private bool _isApplyingSavedPreferences;
    private bool _hasExplicitCliLanguageOverride;
    private string? _savedLanguageFromDisk;
    // GLM-5.3, 2026-11-04：批次 2 阶段 3——通知节流等会话状态迁至 OperationSession；取消令牌仍由 VM 持有。
    private CancellationTokenSource? _cancellationTokenSource;
    // WorkBuddy, 2026-11-16：0.8.13——保存语言事件处理器引用，窗口关闭时才能精确退订；
    // 原来用匿名 lambda 订阅静态单例，进程内永远不会释放对该 ViewModel 的引用。
    private readonly PropertyChangedEventHandler _languageChangedHandler;
    // Gemini 4 Neon, 2026-11-26：批次 34——界面勾选“删除源”前的二次确认保护，防止误删源文件或原归档。
    private readonly DeleteSourceConfirmationGuard _deleteSourceConfirmationGuard = new();

    private readonly OperationTabState _compressionTabState = new();
    private readonly OperationTabState _decompressionTabState = new();
    private bool _isSwitchingOperationTab;
    private int _lastOperationTab;

    private sealed class OperationTabState
    {
        public int SourceMode { get; set; } = 1;
        public string SourcePath { get; set; } = string.Empty;
        public string SaveFilePath { get; set; } = string.Empty;
        public string OutputPath { get; set; } = string.Empty;
        public string SourceFileList { get; set; } = string.Empty;
        public string Extension { get; set; } = "rar";
        public bool UseRandomPassword { get; set; } = true;
        public string CustomPassword { get; set; } = string.Empty;
        public int PasswordNameMode { get; set; }
        public int ExistingFileMode { get; set; } = 2;
        public bool SkipAlreadyProcessed { get; set; } = true;
        public bool DeleteSourceAfter { get; set; }
        public bool MoveSourceAfter { get; set; }
        public bool StrictExtract { get; set; }
        public double MaxSizeGB { get; set; } = 666;
        public bool ShutdownAfterComplete { get; set; }
        public string PasswordQueryFileName { get; set; } = string.Empty;
        public string PasswordQueryResult { get; set; } = string.Empty;
        // WorkBuddy, 2026-11-16：0.8.12——页签状态也是默认值来源之一；
        // 此前这里单独写着“最快 + 开分卷 + 恢复记录 0”，切到解压页会把统一后的默认值覆盖掉。
        public int CompressionLevel { get; set; } = ArchiveDefaults.DefaultCompressionLevel;
        public bool SolidArchive { get; set; } = true;
        public bool EnableVolume { get; set; } = ArchiveDefaults.DefaultEnableVolume;
        public string VolumeSize { get; set; } = ArchiveDefaults.DefaultVolumeSize;
        public int VolumeUnit { get; set; }
        public int RecoveryRecordPercent { get; set; } = ArchiveDefaults.DefaultRecoveryRecordPercent;
        public bool LockArchive { get; set; }
        public bool QuickOpen { get; set; }
        public bool TestArchive { get; set; }
        public bool EnableComment { get; set; } = true;
        public string CommentFilePath { get; set; } = "注释.txt";
        public string TempDirectory { get; set; } = string.Empty;
        public bool AddEnclosures { get; set; } = true;
        public string EnclosureList { get; set; } = string.Empty;
    }

    // 本地化支持。
    public LocalizationService Localization => LocalizationService.Instance;
    public LanguageStrings L => Localization.Strings;

    /// <summary>
    /// 下拉框可用的语言列表。
    /// </summary>
    public List<KeyValuePair<string, string>> AvailableLanguages { get; } =
        LocalizationService.AvailableLanguages.ToList();

    /// <summary>
    /// 当前选中的语言代码。
    /// </summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(L))]
    [NotifyPropertyChangedFor(nameof(BrowseSourceButtonText))]
    [NotifyPropertyChangedFor(nameof(SourcePathWatermark))]
    [NotifyPropertyChangedFor(nameof(SourcePathLabel))]
    [NotifyPropertyChangedFor(nameof(SourceFileListTabHeader))]
    [NotifyPropertyChangedFor(nameof(SuccessLogTabHeader))]
    [NotifyPropertyChangedFor(nameof(FailLogTabHeader))]
    [NotifyPropertyChangedFor(nameof(CommandLogTabHeader))]
    [NotifyPropertyChangedFor(nameof(ProcessingSpeedDisplay))]
    private string _selectedLanguage = "zh-CN";

    partial void OnSelectedLanguageChanged(string value)
    {
        if (!_isApplyingInitialLanguage)
        {
            _hasExplicitCliLanguageOverride = false;
        }

        // 修改本地化服务的当前语言；服务会替换全部界面字符串。
        Localization.CurrentLanguage = value;

        // 强制通知全部依赖本地化字符串的绑定，确保界面立即刷新。
        OnPropertyChanged(nameof(L));

        // 使用 ObservableCollection 重新填充下拉选项，触发控件刷新。
        RefreshAllDropdownOptions();

        // 显式通知所有依赖 L 的计算属性。
        OnPropertyChanged(nameof(BrowseSourceButtonText));
        OnPropertyChanged(nameof(SourcePathWatermark));
        OnPropertyChanged(nameof(SourcePathLabel));
        OnPropertyChanged(nameof(SourceFileListTabHeader));
        OnPropertyChanged(nameof(SuccessLogTabHeader));
        OnPropertyChanged(nameof(FailLogTabHeader));
        OnPropertyChanged(nameof(CommandLogTabHeader));
        OnPropertyChanged(nameof(ProcessingSpeedDisplay));

        // 通知本地化服务的订阅者，使外部绑定也能收到语言切换事件。
        Localization.NotifyPropertyChanged(nameof(LocalizationService.Strings));
    }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(BrowseSourceButtonText))]
    [NotifyPropertyChangedFor(nameof(IsFromTxtMode))]
    private int _sourceMode = 1; // 0 = 当前页 TXT 来源，1 = 当前页目录来源

    /// <summary>
    /// 顶部一级导航：0=压缩配置，1=解压配置，2=开始，3=成功记录，4=失败记录，5=命令日志。
    /// 开始页的两个操作按钮分别使用各自配置页保存的状态。
    /// </summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsCompressionTab))]
    [NotifyPropertyChangedFor(nameof(IsDecompressionTab))]
    [NotifyPropertyChangedFor(nameof(IsStartTab))]
    [NotifyPropertyChangedFor(nameof(IsSuccessLogTab))]
    [NotifyPropertyChangedFor(nameof(IsFailLogTab))]
    [NotifyPropertyChangedFor(nameof(IsCommandLogTab))]
    [NotifyPropertyChangedFor(nameof(IsOperationTab))]
    [NotifyPropertyChangedFor(nameof(SourcePathLabel))]
    // WorkBuddy, 2026-11-16：0.8.13——「开始」页的两个操作按钮直接绑这两个派生属性，
    // 原来漏发通知时会停在上一页的显隐状态。
    [NotifyPropertyChangedFor(nameof(IsCompressionActionVisible))]
    [NotifyPropertyChangedFor(nameof(IsDecompressionActionVisible))]
    private int _activeTab;

    public bool IsCompressionTab => ActiveTab == 0;
    public bool IsDecompressionTab => ActiveTab == 1;
    public bool IsStartTab => ActiveTab == 2;
    public bool IsSuccessLogTab => ActiveTab == 3;
    public bool IsFailLogTab => ActiveTab == 4;
    public bool IsCommandLogTab => ActiveTab == 5;
    public bool IsOperationTab => IsCompressionTab || IsDecompressionTab;
    public bool IsCompressionActionVisible => TabVisibility.IsCompressionActionVisible(ActiveTab);
    public bool IsDecompressionActionVisible => TabVisibility.IsDecompressionActionVisible(ActiveTab);

    private int CurrentOperationTab => ActiveTab is 0 or 1 ? ActiveTab : _lastOperationTab;

    private bool IsCurrentOperationTab(int operationTab) =>
        ActiveTab == operationTab || (IsStartTab && _lastOperationTab == operationTab);

    public string BrowseSourceButtonText => SourceMode == 0 ? L.SelectTxt : L.SelectDirectory;

    public string SourcePathWatermark => SourceMode == 0 ? L.TxtPathWatermark : L.SavePathWatermark;
    public string SourcePathLabel => CurrentOperationTab == 0
        ? SourceMode == 0 ? L.CompressionTxtMode : L.CompressFolderMode
        : SourceMode == 0 ? L.FromTxtMode : L.DecompressFolderMode;

    /// <summary>
    /// 随语言切换动态更新的来源模式选项。
    /// </summary>
    public ObservableCollection<string> SourceModeOptions { get; } = new();

    /// <summary>
    /// 随语言切换动态更新的压缩级别选项。
    /// </summary>
    public ObservableCollection<string> CompressionLevelOptions { get; } = new();

    /// <summary>
    /// 随语言切换动态更新的已有文件处理选项。
    /// </summary>
    public ObservableCollection<string> ExistingFileModeOptions { get; } = new();

    /// <summary>
    /// 随语言切换刷新绑定的分卷单位选项。
    /// 单位文本虽然不随语言变化，但仍使用 ObservableCollection 保证控件正确刷新。
    /// </summary>
    public ObservableCollection<string> VolumeUnitOptions { get; } = new();

    public ObservableCollection<string> PasswordNameModeOptions { get; } = new();

    public bool IsFromTxtMode => SourceMode == 0;

    // 标签页标题附带当前条目数量。
    public string SourceFileListTabHeader => $"{L.FileListTab} ({SourceCollector.SplitLines(SourceFileList).Count})";
    public string SuccessLogTabHeader => $"{L.SuccessLogTab} ({(string.IsNullOrEmpty(SuccessLog) ? 0 : SuccessLog.Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries).Length)})";
    public string FailLogTabHeader => $"{L.FailLogTab} ({(string.IsNullOrEmpty(FailLog) ? 0 : FailLog.Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries).Length)})";
    public string CommandLogTabHeader => $"{L.CommandLogTab} ({(string.IsNullOrEmpty(CommandLog) ? 0 : CommandLog.Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries).Length)})";

    [ObservableProperty]
    private string _sourcePath = string.Empty;

    [ObservableProperty]
    private string _saveFilePath = string.Empty;

    [ObservableProperty]
    private string _outputPath = string.Empty;

    [ObservableProperty]
    private string _textFilePath = string.Empty;

    [ObservableProperty]
    private string _extension = "rar";

    [ObservableProperty]
    private bool _useRandomPassword = true;

    [ObservableProperty]
    private string _customPassword = string.Empty;

    // Gemini 4 Hydrogen, 2026-11-27：批次 44（v0.8.34）——自定义密码掩码/显隐控制（不持久化）。
    [ObservableProperty]
    private bool _showCustomPassword;

    [ObservableProperty]
    private int _passwordNameMode;

    [ObservableProperty]
    // WorkBuddy, 2026-11-16：0.8.12——界面默认压缩级别统一读 ArchiveDefaults（标准），
    // 不再单独写死“最快”，避免同一任务从两个入口跑出不同结果。
    private int _compressionLevel = ArchiveDefaults.DefaultCompressionLevel; // 0-5

    // GLM-5.3, 2026-11-04：批次 5——受控并发度（1-16，默认 1 串行），
    // 两个操作页签共享同一设置（机器级参数，不参与页签状态切换）。
    [ObservableProperty]
    private int _maxConcurrency = 1;

    [ObservableProperty]
    private bool _solidArchive = true;

    [ObservableProperty]
    private bool _enableVolume = ArchiveDefaults.DefaultEnableVolume;

    [ObservableProperty]
    private string _volumeSize = ArchiveDefaults.DefaultVolumeSize;

    [ObservableProperty]
    private int _volumeUnit = 0; // 0=G, 1=M, 2=K；与 DefaultVolumeUnit 的 "g" 对应

    [ObservableProperty]
    private int _recoveryRecordPercent = ArchiveDefaults.DefaultRecoveryRecordPercent;

    [ObservableProperty]
    private int _existingFileMode = 2; // 0=Skip, 1=Update, 2=Overwrite

    [ObservableProperty]
    private bool _lockArchive;

    [ObservableProperty]
    private bool _quickOpen = false;

    [ObservableProperty]
    private bool _testArchive = false;

    [ObservableProperty]
    private bool _enableComment = true;

    [ObservableProperty]
    private string _commentFilePath = "注释.txt";

    [ObservableProperty]
    private string _tempDirectory = string.Empty;

    [ObservableProperty]
    private bool _skipAlreadyProcessed = true;

    [ObservableProperty]
    private bool _deleteSourceAfter = false;

    [ObservableProperty]
    private bool _moveSourceAfter = false;

    [ObservableProperty]
    private bool _strictExtract = false;

    [ObservableProperty]
    private bool _addEnclosures = true;

    [ObservableProperty]
    private string _enclosureList = string.Empty;

    [ObservableProperty]
    private double _maxSizeGB = 666;

    [ObservableProperty]
    private bool _shutdownAfterComplete = false;

    [ObservableProperty]
    private bool _isShutdownScheduled;

    [ObservableProperty]
    private string _currentFile = "Ready";

    [ObservableProperty]
    private string _currentSourcePath = string.Empty;

    [ObservableProperty]
    private int _successCount = 0;

    [ObservableProperty]
    private int _failCount = 0;

    [ObservableProperty]
    private int _postProcessFailCount = 0;

    [ObservableProperty]
    private int _ignoreCount = 0;

    [ObservableProperty]
    private int _nonExistCount = 0;

    [ObservableProperty]
    private int _incompleteVolumeCount = 0;

    [ObservableProperty]
    private int _ambiguousArchiveCount = 0;

    [ObservableProperty]
    private double _processedSizeGB = 0;

    [ObservableProperty]
    private double _totalSizeGB = 0;

    [ObservableProperty]
    private TimeSpan _elapsedTime = TimeSpan.Zero;

    [ObservableProperty]
    private TimeSpan _remainingTime = TimeSpan.Zero;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ProcessingSpeedDisplay))]
    private double _processingSpeedMBPerSecond = 0;

    /// <summary>
    /// 返回带本地化单位的处理速度显示文本。
    /// </summary>
    public string ProcessingSpeedDisplay => $"{ProcessingSpeedMBPerSecond:0}{L.ProcessingSpeedUnit}";

    [ObservableProperty]
    private DateTime _estimatedCompletionTime = DateTime.MinValue;

    [ObservableProperty]
    private string _outputSizeText = "0.0GB";

    [ObservableProperty]
    private bool _isOperating = false;

    // Antigravity, 2026-11-08：批次 17（P3-11/P3-14）——当前子项/分卷实时进度百分比（0-100）及显示文本。
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(SubProgressDisplay))]
    private int _subProgressPercent = 0;

    [ObservableProperty]
    private bool _hasSubProgress = false;

    public string SubProgressDisplay => $"{SubProgressPercent}%";

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(SourceFileListTabHeader))]
    private string _sourceFileList = string.Empty;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(SuccessLogTabHeader))]
    [NotifyPropertyChangedFor(nameof(FilteredSuccessLog))]
    private string _successLog = string.Empty;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(FailLogTabHeader))]
    [NotifyPropertyChangedFor(nameof(FilteredFailLog))]
    private string _failLog = string.Empty;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CommandLogTabHeader))]
    [NotifyPropertyChangedFor(nameof(FilteredCommandLog))]
    private string _commandLog = string.Empty;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(FilteredSuccessLog))]
    private string _successLogFilter = string.Empty;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(FilteredFailLog))]
    private string _failLogFilter = string.Empty;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(FilteredCommandLog))]
    private string _commandLogFilter = string.Empty;

    public string FilteredSuccessLog => LogSearchFilter.FilterLines(SuccessLog, SuccessLogFilter);
    public string FilteredFailLog => LogSearchFilter.FilterLines(FailLog, FailLogFilter);
    public string FilteredCommandLog => LogSearchFilter.FilterLines(CommandLog, CommandLogFilter);

    // WorkBuddy, 2026-11-16：批次 25（P2）——缩放按钮文案由窗口状态驱动，
    // 视图只负责把 WindowState 同步到这里，不再直接改按钮内容。
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ZoomButtonText))]
    private bool _isMaximized;

    public string ZoomButtonText => IsMaximized ? L.ZoomOut : L.ZoomIn;

    [ObservableProperty]
    private string _passwordQueryFileName = string.Empty;

    [ObservableProperty]
    private string _passwordQueryResult = string.Empty;

    // GPT-5, 2026-09-30：GUI 日志写到与窗口设置相同的用户数据目录（macOS 由 ApplicationData 映射），
    // 不写入应用包内部；日志是辅助能力，创建失败不能阻止窗口启动。
    // WorkBuddy, 2026-11-16：批次 27——目录改由 ApplicationInfoService 统一提供，
    // 保证「关于」弹窗显示的路径就是这里实际写入的路径。
    private static FileLoggerService? CreateGuiFileLogger()
    {
        try
        {
            var logPath = Path.Combine(
                ApplicationInfoService.ResolveGuiLogDirectory(),
                $"batchcompress_gui_{DateTime.Now:yyyyMMdd_HHmmss}.log");
            return new FileLoggerService(logPath);
        }
        catch
        {
            return null;
        }
    }

    public MainWindowViewModel(
        EnclosureConfigService? enclosureConfig = null,
        UserPreferencesService? preferencesService = null)
    {
        // GLM-5.3, 2026-11-04：批次 9——附件配置服务可注入；默认行为与批次 8 一致。
        _enclosureConfig = enclosureConfig ?? new EnclosureConfigService();
        _preferencesService = preferencesService ?? new UserPreferencesService();

        // GPT-5, 2026-08-06：按归档格式选择 WinRAR/RAR 或官方 7zz，界面不直接依赖具体命令行工具。
        _archiveEngine = new ArchiveEngineRouter();
        _systemIntegration = new SystemIntegrationService();
        // GPT-5, 2026-09-30：GUI 把文件日志器注入批处理服务，使后处理失败原因等诊断留痕；设计器模式不写日志。
        _fileLogger = Design.IsDesignMode ? null : CreateGuiFileLogger();
        _batchOperationService = new BatchOperationService(_archiveEngine, _systemIntegration, _fileLogger);

        // WorkBuddy, 2026-11-16：批次 25（P2）——缩放按钮文案跟随语言切换刷新。
        // 批次 28 后续：改用可保存的处理器，关闭窗口时退订，避免静态服务长期持有 ViewModel。
        _languageChangedHandler = (_, e) =>
        {
            if (e.PropertyName == nameof(BatchCompress.Avalonia.Localization.LocalizationService.Strings))
            {
                OnPropertyChanged(nameof(ZoomButtonText));
            }
        };
        LocalizationService.Instance.PropertyChanged += _languageChangedHandler;

        // GPT-5, 2026-08-05：在绑定渲染前填充可观察选项集合，并在语言变化时刷新。
        RefreshAllDropdownOptions();

        // 设计器模式不执行运行时初始化。
        if (Design.IsDesignMode)
        {
            // 直接设置字段，避免设计器加载时触发异步任务。
            _sourceMode = 1;
            // GLM-5.3, 2026-11-04：批次 8——联系方式外置到 EnclosureConfigService；
            // 设计器只取默认模版（纯字符串，不做任何文件 IO）。
            _enclosureList = EnclosureConfigService.GetDefaultEnclosuresText();
            return;
        }

        var savedPreferences = _preferencesService.Load();
        _savedLanguageFromDisk = savedPreferences?.Language;

        _isApplyingInitialLanguage = true;
        try
        {
            // WorkBuddy, 2026-11-16：0.8.13——命令行显式给了 --lang 时，界面必须沿用该语言；
            // 原来的无条件系统语言检测会把命令行指定的语言覆盖掉。
            if (LocalizationService.Instance.LanguageExplicitlySet)
            {
                _hasExplicitCliLanguageOverride = true;
                SelectedLanguage = LocalizationService.Instance.CurrentLanguage;
            }
            else if (savedPreferences?.Language is { Length: > 0 } savedLanguage &&
                     LocalizationService.AvailableLanguages.ContainsKey(savedLanguage))
            {
                SelectedLanguage = savedLanguage;
            }
            else
            {
                // 检测并设置系统语言。
                DetectSystemLanguage();
            }
        }
        finally
        {
            _isApplyingInitialLanguage = false;
        }

        // 设置运行时默认值。
        SourceMode = 1;
        // GLM-5.3, 2026-11-04：批次 8——附件联系方式不再硬编码：
        // 优先读用户配置文件（~/.config/batchcompress/enclosures.txt 或应用目录相邻），
        // 不存在或为空时回退默认联系文案模版。
        // GLM-5.3, 2026-11-04：批次 9——改用注入的配置服务实例（测试可隔离）。
        var loadedEnclosures = _enclosureConfig.LoadEnclosuresText();
        _compressionTabState.EnclosureList = loadedEnclosures;
        _decompressionTabState.EnclosureList = loadedEnclosures;
        EnclosureList = loadedEnclosures;

        if (savedPreferences is not null)
        {
            ApplySavedPreferences(savedPreferences);
        }

        // WorkBuddy, 2026-11-16：0.8.12——把界面实际默认值写进运行日志，
        // 便于核对两个入口是否真的统一，也便于排查“为什么这次没有分卷”。
        // 说明：用户在界面上的后续修改会保存在页签状态里，不受这里的默认值影响。
        var volumeUnit = VolumeUnit switch { 1 => "m", 2 => "k", _ => "g" };
        var volumeText = EnableVolume ? $"{VolumeSize}{volumeUnit}" : "none";
        _fileLogger?.LogOperation(
            "GUI_DEFAULTS",
            $"language={SelectedLanguage}, level={CompressionLevel}, " +
            $"recovery={RecoveryRecordPercent}%, volume={volumeText}");

        // GPT-5, 2026-08-05：仅在剪贴板文本是现有目录时采用，绝不把任意复制文本作为路径。
        Dispatcher.UIThread.Post(async () =>
        {
            try
            {
                var clipboardText = await _systemIntegration.ReadClipboardTextAsync();
                if (!string.IsNullOrEmpty(clipboardText) && Directory.Exists(clipboardText))
                {
                    SaveFilePath = clipboardText;
                }
            }
            catch
            {
                // 启动时读取剪贴板失败不应阻止应用打开。
            }
        });
    }

    // 由视图设置的文件选择回调。
    public Func<Task>? BrowseSourceRequested { get; set; }
    public Func<Task>? BrowseOutputRequested { get; set; }
    public Func<Task>? BrowseTextFileRequested { get; set; }
    public Func<Task>? BrowseSaveFileRequested { get; set; }
    public Func<Task>? BrowseAttachmentRequested { get; set; }
    public Func<Task>? ShowHelpRequested { get; set; }
    // WorkBuddy, 2026-11-16：批次 27——「关于」窗口由视图创建，ViewModel 只发出请求。
    public Func<Task>? ShowAboutRequested { get; set; }
    public Action? HideWindowRequested { get; set; }
    public Func<Task<bool>>? ConfirmShutdownCancellationRequested { get; set; }
    public Func<Task<bool>>? ConfirmDeleteSourceRequested { get; set; }
    public Func<Task<bool>>? ConfirmResetPreferencesRequested { get; set; }
    public Func<string, string, Task>? ShowPreflightDialogRequested { get; set; }
    public Action? FocusLogSearchRequested { get; set; }

    partial void OnSourceModeChanged(int value)
    {
        if (_isSwitchingOperationTab)
        {
            return;
        }

        // 来源模式改变后重新加载文件列表。
        QueueRefresh(includeTotalSize: false);
    }

    partial void OnSourcePathChanged(string value)
    {
        if (_isSwitchingOperationTab)
        {
            return;
        }

        if (!string.IsNullOrEmpty(value))
        {
            // 当前页的 TXT 来源在路径改变后重新解析清单。
            if (SourceMode == 0)
            {
                QueueRefresh(includeTotalSize: true, delay: true);
            }
        }
        else
        {
            if (SourceMode == 0)
            {
                TotalSizeGB = 0;
            }
        }
    }

    partial void OnOutputPathChanged(string value)
    {
        if (_isSwitchingOperationTab)
        {
            return;
        }

        UpdateOutputSize();

        if (!string.IsNullOrEmpty(value))
        {
            TempDirectory = value;
        }
    }

    partial void OnExtensionChanged(string value)
    {
        RefreshPasswordNameModeOptions();

        if (_isSwitchingOperationTab)
        {
            return;
        }

        // GPT-5, 2026-08-06：7z 已由官方 7zz 完整支持；这里只提示该格式不具备 RAR 专属恢复记录和快速打开能力。
        if (value.Trim().TrimStart('.').Equals("7z", StringComparison.OrdinalIgnoreCase))
        {
            AppendCommandLog(L.Warning7zFormat + "\n");
        }
    }

    partial void OnCompressionLevelChanged(int value)
    {
        if (_isSwitchingOperationTab)
        {
            return;
        }

        // 选择存储模式时关闭固实压缩，因为该组合没有意义。
        if (value == 0 && SolidArchive)
        {
            SolidArchive = false;
            AppendCommandLog(L.SolidDisabledForStore + "\n");
        }
    }

    partial void OnDeleteSourceAfterChanged(bool value)
    {
        if (_isSwitchingOperationTab || _deleteSourceConfirmationGuard.IsConfirming)
        {
            return;
        }

        if (!value)
        {
            return;
        }

        if (ConfirmDeleteSourceRequested != null)
        {
            _ = ConfirmDeleteSourceAfterAsync();
            return;
        }

        if (MoveSourceAfter)
        {
            MoveSourceAfter = false;
        }
    }

    private async Task ConfirmDeleteSourceAfterAsync()
    {
        var confirmed = await _deleteSourceConfirmationGuard.ConfirmEnableAsync(ConfirmDeleteSourceRequested);
        if (!confirmed)
        {
            DeleteSourceAfter = false;
            return;
        }

        if (MoveSourceAfter)
        {
            MoveSourceAfter = false;
        }
    }

    partial void OnMoveSourceAfterChanged(bool value)
    {
        if (_isSwitchingOperationTab)
        {
            return;
        }

        if (value && DeleteSourceAfter)
        {
            DeleteSourceAfter = false;
        }
    }

    partial void OnSaveFilePathChanged(string value)
    {
        if (_isSwitchingOperationTab)
        {
            return;
        }

        if (!string.IsNullOrEmpty(value))
        {
            QueueRefresh(includeTotalSize: true, delay: true);
        }
        else
        {
            TotalSizeGB = 0;
        }
    }

}
