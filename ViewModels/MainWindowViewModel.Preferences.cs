using System;
using System.IO;
using System.Threading.Tasks;
using Avalonia.Controls;
using BatchCompress.Avalonia.Core.Services;
using BatchCompress.Avalonia.Localization;
using CommunityToolkit.Mvvm.Input;

namespace BatchCompress.Avalonia.ViewModels;

// Gemini 4 Hydrogen, 2026-11-27：批次 45（v0.8.35）——从 MainWindowViewModel.cs 拆出的语言/下拉选项、
// 操作页签切换及安全用户偏好持久化协调分部类。
public partial class MainWindowViewModel
{
    // GPT-5, 2026-08-07：密码依据提示跟随当前归档扩展名变化，但选项刷新不能改变用户已经选择的模式。
    private void RefreshPasswordNameModeOptions()
    {
        var currentPasswordNameMode = PasswordNameMode;
        var normalizedExtension = Extension.Trim().TrimStart('.').ToLowerInvariant();
        if (normalizedExtension.Length == 0)
        {
            normalizedExtension = "rar";
        }

        PasswordNameModeOptions.Clear();
        // GPT-5, 2026-09-30：密码依据选项改用本地化词条，随界面语言刷新。
        PasswordNameModeOptions.Add(string.Format(L.PasswordNameModeFileNameWithExtension, normalizedExtension));
        PasswordNameModeOptions.Add(L.PasswordNameModeFileName);
        PasswordNameMode = currentPasswordNameMode >= 0 && currentPasswordNameMode < PasswordNameModeOptions.Count
            ? currentPasswordNameMode
            : 0;
    }

    /// <summary>
    /// 使用当前语言重新填充全部下拉选项，确保 ComboBox 显示文本同步。
    /// </summary>
    private void RefreshAllDropdownOptions()
    {
        // 保存当前选择，重新填充后恢复有效索引。
        var currentSourceMode = SourceMode;
        var currentCompressionLevel = CompressionLevel;
        var currentExistingFileMode = ExistingFileMode;
        var currentVolumeUnit = VolumeUnit;

        // 当前操作页只展示与自身业务匹配的 TXT 和目录来源。
        RefreshSourceModeOptions(CurrentOperationTab, currentSourceMode);

        // 清空并重新填充压缩级别选项。
        CompressionLevelOptions.Clear();
        CompressionLevelOptions.Add(L.NoCompression);
        CompressionLevelOptions.Add(L.Light);
        CompressionLevelOptions.Add(L.Fast);
        CompressionLevelOptions.Add(L.Standard);
        CompressionLevelOptions.Add(L.Better);
        CompressionLevelOptions.Add(L.Best);

        // 清空并重新填充已有文件处理选项。
        ExistingFileModeOptions.Clear();
        ExistingFileModeOptions.Add(L.SkipExisting);
        ExistingFileModeOptions.Add(L.UpdateExisting);
        ExistingFileModeOptions.Add(L.OverwriteExisting);

        // 清空并重新填充分卷单位选项。
        VolumeUnitOptions.Clear();
        VolumeUnitOptions.Add("GB");
        VolumeUnitOptions.Add("MB");
        VolumeUnitOptions.Add("KB");

        RefreshPasswordNameModeOptions();

        // 集合重建后恢复有效索引，使 ComboBox 显示正确文本。
        CompressionLevel = currentCompressionLevel >= 0 && currentCompressionLevel < CompressionLevelOptions.Count ? currentCompressionLevel : 0;
        ExistingFileMode = currentExistingFileMode >= 0 && currentExistingFileMode < ExistingFileModeOptions.Count ? currentExistingFileMode : 0;
        VolumeUnit = currentVolumeUnit >= 0 && currentVolumeUnit < VolumeUnitOptions.Count ? currentVolumeUnit : 0;
    }

    private void RefreshSourceModeOptions(int tab, int requestedMode)
    {
        SourceModeOptions.Clear();
        if (tab == 1)
        {
            SourceModeOptions.Add(L.FromTxtMode);
            SourceModeOptions.Add(L.DecompressFolderMode);
        }
        else
        {
            SourceModeOptions.Add(L.CompressionTxtMode);
            SourceModeOptions.Add(L.CompressFolderMode);
        }

        SourceMode = requestedMode is 0 or 1 ? requestedMode : 1;
    }

    /// <summary>
    /// 检测系统语言并设置对应的界面语言。
    /// </summary>
    private void DetectSystemLanguage()
    {
        try
        {
            var systemCulture = System.Globalization.CultureInfo.CurrentUICulture;
            var cultureName = systemCulture.Name;
            var twoLetterISOLanguageName = systemCulture.TwoLetterISOLanguageName;

            // 将系统区域性映射到应用支持的语言。
            string languageCode;
            if (twoLetterISOLanguageName == "zh")
            {
                // 中文根据区域代码区分简体和繁体。
                if (cultureName == "zh-TW" || cultureName == "zh-HK" || cultureName == "zh-MO" || cultureName == "zh-Hant")
                {
                    languageCode = "zh-TW"; // 繁体中文。
                }
                else
                {
                    languageCode = "zh-CN"; // 简体中文。
                }
            }
            else if (twoLetterISOLanguageName == "ja")
            {
                languageCode = "ja"; // 日语。
            }
            else if (twoLetterISOLanguageName == "de")
            {
                languageCode = "de"; // 德语。
            }
            else if (twoLetterISOLanguageName == "en")
            {
                languageCode = "en"; // 英语。
            }
            else
            {
                // 未匹配时默认使用简体中文。
                languageCode = "zh-CN";
            }

            // 只有在资源存在时才切换语言。
            if (LocalizationService.AvailableLanguages.ContainsKey(languageCode))
            {
                SelectedLanguage = languageCode;
            }
        }
        catch (System.Globalization.CultureNotFoundException ex)
        {
            // 区域性检测失败时保留简体中文默认值。
            System.Diagnostics.Debug.WriteLine($"Language detection failed: {ex.Message}");
        }
    }

    partial void OnActiveTabChanged(int oldValue, int newValue)
    {
        if (_isApplyingSavedPreferences)
        {
            return;
        }

        if (oldValue is 0 or 1)
        {
            SaveOperationTabState(oldValue == 0 ? _compressionTabState : _decompressionTabState);
            SetLastOperationTab(oldValue);
        }

        if (newValue is not (0 or 1))
        {
            RefreshSourceModeOptions(_lastOperationTab, SourceMode);
            OnPropertyChanged(nameof(SourcePathLabel));
            return;
        }

        SetLastOperationTab(newValue);
        LoadOperationTabState(newValue);

        OnPropertyChanged(nameof(SourcePathLabel));
        QueueRefresh(includeTotalSize: true);
    }

    private void ActivateOperationStateForStart(int operationTab)
    {
        if (!IsStartTab || operationTab is not (0 or 1) || _lastOperationTab == operationTab)
        {
            return;
        }

        SaveOperationTabState(_lastOperationTab == 0 ? _compressionTabState : _decompressionTabState);
        SetLastOperationTab(operationTab);
        LoadOperationTabState(operationTab);
        OnPropertyChanged(nameof(SourcePathLabel));
    }

    private void LoadOperationTabState(int operationTab)
    {
        var state = operationTab == 0 ? _compressionTabState : _decompressionTabState;
        _isSwitchingOperationTab = true;
        try
        {
            RefreshSourceModeOptions(operationTab, state.SourceMode);
            SourcePath = state.SourcePath;
            SaveFilePath = state.SaveFilePath;
            OutputPath = state.OutputPath;
            SourceFileList = state.SourceFileList;
            Extension = state.Extension;
            UseRandomPassword = state.UseRandomPassword;
            CustomPassword = state.CustomPassword;
            PasswordNameMode = state.PasswordNameMode;
            ExistingFileMode = state.ExistingFileMode;
            SkipAlreadyProcessed = state.SkipAlreadyProcessed;
            DeleteSourceAfter = state.DeleteSourceAfter;
            MoveSourceAfter = state.MoveSourceAfter;
            StrictExtract = state.StrictExtract;
            MaxSizeGB = state.MaxSizeGB;
            ShutdownAfterComplete = state.ShutdownAfterComplete;
            PasswordQueryFileName = state.PasswordQueryFileName;
            PasswordQueryResult = state.PasswordQueryResult;
            CompressionLevel = state.CompressionLevel;
            SolidArchive = state.SolidArchive;
            EnableVolume = state.EnableVolume;
            VolumeSize = state.VolumeSize;
            VolumeUnit = state.VolumeUnit;
            RecoveryRecordPercent = state.RecoveryRecordPercent;
            LockArchive = state.LockArchive;
            QuickOpen = state.QuickOpen;
            TestArchive = state.TestArchive;
            EnableComment = state.EnableComment;
            CommentFilePath = state.CommentFilePath;
            TempDirectory = state.TempDirectory;
            AddEnclosures = state.AddEnclosures;
            EnclosureList = state.EnclosureList;
        }
        finally
        {
            _isSwitchingOperationTab = false;
        }
    }

    private void SetLastOperationTab(int operationTab)
    {
        if (operationTab is not (0 or 1) || _lastOperationTab == operationTab)
        {
            return;
        }

        _lastOperationTab = operationTab;
        OnPropertyChanged(nameof(IsCompressionActionVisible));
        OnPropertyChanged(nameof(IsDecompressionActionVisible));
        OnPropertyChanged(nameof(SourcePathLabel));
    }

    private void SaveOperationTabState(OperationTabState state)
    {
        state.SourceMode = SourceMode;
        state.SourcePath = SourcePath;
        state.SaveFilePath = SaveFilePath;
        state.OutputPath = OutputPath;
        state.SourceFileList = SourceFileList;
        state.Extension = Extension;
        state.UseRandomPassword = UseRandomPassword;
        state.CustomPassword = CustomPassword;
        state.PasswordNameMode = PasswordNameMode;
        state.ExistingFileMode = ExistingFileMode;
        state.SkipAlreadyProcessed = SkipAlreadyProcessed;
        state.DeleteSourceAfter = DeleteSourceAfter;
        state.MoveSourceAfter = MoveSourceAfter;
        state.StrictExtract = StrictExtract;
        state.MaxSizeGB = MaxSizeGB;
        state.ShutdownAfterComplete = ShutdownAfterComplete;
        state.PasswordQueryFileName = PasswordQueryFileName;
        state.PasswordQueryResult = PasswordQueryResult;
        state.CompressionLevel = CompressionLevel;
        state.SolidArchive = SolidArchive;
        state.EnableVolume = EnableVolume;
        state.VolumeSize = VolumeSize;
        state.VolumeUnit = VolumeUnit;
        state.RecoveryRecordPercent = RecoveryRecordPercent;
        state.LockArchive = LockArchive;
        state.QuickOpen = QuickOpen;
        state.TestArchive = TestArchive;
        state.EnableComment = EnableComment;
        state.CommentFilePath = CommentFilePath;
        state.TempDirectory = TempDirectory;
        state.AddEnclosures = AddEnclosures;
        state.EnclosureList = EnclosureList;
    }

    // Gemini 4 Neon, 2026-11-27：批次 40（v0.8.29）——启动恢复与退出保存非敏感 GUI 偏好。
    private void ApplySavedPreferences(UserPreferencesSnapshot preferences)
    {
        ApplyTabPreferences(_compressionTabState, preferences.CompressionTab);
        ApplyTabPreferences(_decompressionTabState, preferences.DecompressionTab);

        MaxConcurrency = preferences.MaxConcurrency;

        var targetTab = preferences.ActiveOperationTab is 0 or 1 ? preferences.ActiveOperationTab : 0;
        _lastOperationTab = targetTab;
        LoadOperationTabState(targetTab);

        if (targetTab != ActiveTab)
        {
            _isApplyingSavedPreferences = true;
            try
            {
                ActiveTab = targetTab;
            }
            finally
            {
                _isApplyingSavedPreferences = false;
            }
        }
    }

    private void SaveUserPreferences()
    {
        if (Design.IsDesignMode)
        {
            return;
        }

        SaveOperationTabState(CurrentOperationTab == 0 ? _compressionTabState : _decompressionTabState);
        var languageToSave = _hasExplicitCliLanguageOverride
            ? _savedLanguageFromDisk
            : SelectedLanguage;

        _preferencesService.Save(new UserPreferencesSnapshot
        {
            Language = languageToSave,
            MaxConcurrency = MaxConcurrency,
            ActiveOperationTab = CurrentOperationTab,
            CompressionTab = ExtractTabPreferences(_compressionTabState),
            DecompressionTab = ExtractTabPreferences(_decompressionTabState)
        });
    }

    // Gemini 4 Hydrogen, 2026-11-27：批次 41（v0.8.30）——一键重置安全偏好并清除磁盘偏好文件。
    [RelayCommand]
    private async Task ResetPreferencesAsync()
    {
        if (IsOperating)
        {
            return;
        }

        if (ConfirmResetPreferencesRequested != null && !await ConfirmResetPreferencesRequested())
        {
            return;
        }

        _preferencesService.Reset();
        _savedLanguageFromDisk = null;

        _isApplyingInitialLanguage = true;
        try
        {
            if (LocalizationService.Instance.LanguageExplicitlySet)
            {
                _hasExplicitCliLanguageOverride = true;
                SelectedLanguage = LocalizationService.Instance.CurrentLanguage;
            }
            else
            {
                _hasExplicitCliLanguageOverride = false;
                DetectSystemLanguage();
            }
        }
        finally
        {
            _isApplyingInitialLanguage = false;
        }

        ApplySavedPreferences(UserPreferencesSnapshot.CreateDefault());
        AppendCommandLog(L.PreferencesResetLog + "\n");
    }

    private static void ApplyTabPreferences(OperationTabState target, OperationTabPreferences preferences)
    {
        target.SourceMode = preferences.SourceMode;
        target.Extension = preferences.Extension;
        target.UseRandomPassword = preferences.UseRandomPassword;
        target.PasswordNameMode = preferences.PasswordNameMode;
        target.CompressionLevel = preferences.CompressionLevel;
        target.SolidArchive = preferences.SolidArchive;
        target.EnableVolume = preferences.EnableVolume;
        target.VolumeSize = preferences.VolumeSize;
        target.VolumeUnit = preferences.VolumeUnit;
        target.RecoveryRecordPercent = preferences.RecoveryRecordPercent;
        target.ExistingFileMode = preferences.ExistingFileMode;
        target.LockArchive = preferences.LockArchive;
        target.QuickOpen = preferences.QuickOpen;
        target.TestArchive = preferences.TestArchive;
        target.EnableComment = preferences.EnableComment;
        target.CommentFilePath = preferences.CommentFilePath;
        target.SkipAlreadyProcessed = preferences.SkipAlreadyProcessed;
        target.MoveSourceAfter = preferences.MoveSourceAfter;
        target.StrictExtract = preferences.StrictExtract;
        target.MaxSizeGB = preferences.MaxSizeGB;
        target.AddEnclosures = preferences.AddEnclosures;
        target.CustomPassword = string.Empty;
        target.DeleteSourceAfter = false;
        target.ShutdownAfterComplete = false;
    }

    private static OperationTabPreferences ExtractTabPreferences(OperationTabState source) => new()
    {
        SourceMode = source.SourceMode,
        Extension = source.Extension,
        UseRandomPassword = source.UseRandomPassword,
        PasswordNameMode = source.PasswordNameMode,
        CompressionLevel = source.CompressionLevel,
        SolidArchive = source.SolidArchive,
        EnableVolume = source.EnableVolume,
        VolumeSize = source.VolumeSize,
        VolumeUnit = source.VolumeUnit,
        RecoveryRecordPercent = source.RecoveryRecordPercent,
        ExistingFileMode = source.ExistingFileMode,
        LockArchive = source.LockArchive,
        QuickOpen = source.QuickOpen,
        TestArchive = source.TestArchive,
        EnableComment = source.EnableComment,
        CommentFilePath = source.CommentFilePath,
        SkipAlreadyProcessed = source.SkipAlreadyProcessed,
        MoveSourceAfter = source.MoveSourceAfter,
        StrictExtract = source.StrictExtract,
        MaxSizeGB = source.MaxSizeGB,
        AddEnclosures = source.AddEnclosures
    };
}
