using System;
using System.IO;
using System.Text.Json;
using BatchCompress.Avalonia.Core.Models;

namespace BatchCompress.Avalonia.Core.Services;

/// <summary>
/// 单个操作页签（压缩配置 / 解压配置）的安全可持久化用户偏好。
/// 刻意排除 CustomPassword、DeleteSourceAfter、ShutdownAfterComplete 及单次会话路径，
/// 避免敏感凭证明文落盘或下次启动误删/误关机。
/// </summary>
public sealed record OperationTabPreferences
{
    public int SourceMode { get; init; } = 1;
    public string Extension { get; init; } = "rar";
    public bool UseRandomPassword { get; init; } = true;
    public int PasswordNameMode { get; init; }
    public int CompressionLevel { get; init; } = ArchiveDefaults.DefaultCompressionLevel;
    public bool SolidArchive { get; init; } = true;
    public bool EnableVolume { get; init; } = ArchiveDefaults.DefaultEnableVolume;
    public string VolumeSize { get; init; } = ArchiveDefaults.DefaultVolumeSize;
    public int VolumeUnit { get; init; }
    public int RecoveryRecordPercent { get; init; } = ArchiveDefaults.DefaultRecoveryRecordPercent;
    public int ExistingFileMode { get; init; } = 2;
    public bool LockArchive { get; init; }
    public bool QuickOpen { get; init; }
    public bool TestArchive { get; init; }
    public bool EnableComment { get; init; } = true;
    public string CommentFilePath { get; init; } = "注释.txt";
    public bool SkipAlreadyProcessed { get; init; } = true;
    public bool MoveSourceAfter { get; init; }
    public bool StrictExtract { get; init; }
    public double MaxSizeGB { get; init; } = 666;
    public bool AddEnclosures { get; init; } = true;

    public static OperationTabPreferences CreateDefault() => new();
}

/// <summary>
/// 图形界面安全用户偏好快照。
/// </summary>
public sealed record UserPreferencesSnapshot
{
    public int SchemaVersion { get; init; } = 1;
    public string? Language { get; init; }
    public int MaxConcurrency { get; init; } = 1;
    public int ActiveOperationTab { get; init; }
    public OperationTabPreferences CompressionTab { get; init; } = OperationTabPreferences.CreateDefault();
    public OperationTabPreferences DecompressionTab { get; init; } = OperationTabPreferences.CreateDefault();

    public static UserPreferencesSnapshot CreateDefault() => new();
}

/// <summary>
/// 图形界面用户偏好持久化服务。
/// Gemini 4 Neon, 2026-11-27：批次 40（v0.8.29）——参考 WindowStateService 落盘到同级用户数据目录，
/// 原子写入临时文件后替换目标文件，读取失败或损坏时安全回退 null；不引用 Avalonia 类型便于单测。
/// </summary>
public sealed class UserPreferencesService
{
    private const int CurrentSchemaVersion = 1;
    private static readonly JsonSerializerOptions SerializerOptions = new()
    {
        WriteIndented = true
    };

    public UserPreferencesService(string? settingsPath = null)
    {
        SettingsPath = settingsPath ?? DefaultSettingsPath;
    }

    public string SettingsPath { get; }

    /// <summary>与 WindowStateService 同目录的用户偏好文件默认路径。</summary>
    public static string DefaultSettingsPath
    {
        get
        {
            var applicationData = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
            if (string.IsNullOrWhiteSpace(applicationData))
            {
                applicationData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
            }

            return Path.Combine(
                string.IsNullOrWhiteSpace(applicationData) ? AppContext.BaseDirectory : applicationData,
                ApplicationInfoService.GuiLogFolderName,
                "user-preferences.json");
        }
    }

    /// <summary>对数字范围与必填默认值做归一化，防止手工编辑或旧版本值越界。</summary>
    public static UserPreferencesSnapshot Normalize(UserPreferencesSnapshot snapshot)
    {
        ArgumentNullException.ThrowIfNull(snapshot);

        return new UserPreferencesSnapshot
        {
            SchemaVersion = CurrentSchemaVersion,
            Language = string.IsNullOrWhiteSpace(snapshot.Language) ? null : snapshot.Language.Trim(),
            MaxConcurrency = Math.Clamp(snapshot.MaxConcurrency, 1, 16),
            ActiveOperationTab = Math.Clamp(snapshot.ActiveOperationTab, 0, 1),
            CompressionTab = NormalizeTab(snapshot.CompressionTab),
            DecompressionTab = NormalizeTab(snapshot.DecompressionTab)
        };
    }

    /// <summary>读取失败或文件不存在时返回 null，绝不阻止主窗口启动。</summary>
    public UserPreferencesSnapshot? Load()
    {
        try
        {
            if (!File.Exists(SettingsPath))
            {
                return null;
            }

            var dto = JsonSerializer.Deserialize<PreferencesDto>(
                File.ReadAllText(SettingsPath), SerializerOptions);
            if (dto is null)
            {
                return null;
            }

            return Normalize(new UserPreferencesSnapshot
            {
                SchemaVersion = dto.SchemaVersion,
                Language = dto.Language,
                MaxConcurrency = dto.MaxConcurrency,
                ActiveOperationTab = dto.ActiveOperationTab,
                CompressionTab = ToTabPreferences(dto.CompressionTab),
                DecompressionTab = ToTabPreferences(dto.DecompressionTab)
            });
        }
        catch
        {
            return null;
        }
    }

    /// <summary>原子写入用户偏好；失败时静默忽略，避免阻塞窗口关闭或应用退出。</summary>
    public void Save(UserPreferencesSnapshot snapshot)
    {
        ArgumentNullException.ThrowIfNull(snapshot);

        string? tempPath = null;
        try
        {
            var directory = Path.GetDirectoryName(SettingsPath);
            if (!string.IsNullOrEmpty(directory))
            {
                Directory.CreateDirectory(directory);
            }

            var normalized = Normalize(snapshot);
            var dto = new PreferencesDto
            {
                SchemaVersion = normalized.SchemaVersion,
                Language = normalized.Language,
                MaxConcurrency = normalized.MaxConcurrency,
                ActiveOperationTab = normalized.ActiveOperationTab,
                CompressionTab = FromTabPreferences(normalized.CompressionTab),
                DecompressionTab = FromTabPreferences(normalized.DecompressionTab)
            };

            tempPath = $"{SettingsPath}.{Guid.NewGuid():N}.tmp";
            File.WriteAllText(tempPath, JsonSerializer.Serialize(dto, SerializerOptions));
            File.Move(tempPath, SettingsPath, overwrite: true);
        }
        catch
        {
            // 用户偏好持久化失败不能阻塞窗口关闭或退出。
        }
        finally
        {
            if (!string.IsNullOrEmpty(tempPath) && File.Exists(tempPath))
            {
                try
                {
                    File.Delete(tempPath);
                }
                catch
                {
                    // 清理临时文件失败时忽略。
                }
            }
        }
    }

    /// <summary>删除已保存的用户偏好文件；文件不存在或删除失败时不抛异常。</summary>
    public void Reset()
    {
        try
        {
            if (File.Exists(SettingsPath))
            {
                File.Delete(SettingsPath);
            }
        }
        catch
        {
            // 重置偏好失败不应阻塞界面交互。
        }
    }

    private static OperationTabPreferences NormalizeTab(OperationTabPreferences? tab)
    {
        var source = tab ?? OperationTabPreferences.CreateDefault();
        var compressionLevel = Math.Clamp(source.CompressionLevel, 0, 5);
        return new OperationTabPreferences
        {
            SourceMode = Math.Clamp(source.SourceMode, 0, 1),
            Extension = string.IsNullOrWhiteSpace(source.Extension) ? "rar" : source.Extension.Trim(),
            UseRandomPassword = source.UseRandomPassword,
            PasswordNameMode = Math.Clamp(source.PasswordNameMode, 0, 1),
            CompressionLevel = compressionLevel,
            // 仅存储（级别 0）时固实压缩无意义，与界面规则保持一致。
            SolidArchive = compressionLevel != 0 && source.SolidArchive,
            EnableVolume = source.EnableVolume,
            VolumeSize = string.IsNullOrWhiteSpace(source.VolumeSize)
                ? ArchiveDefaults.DefaultVolumeSize
                : source.VolumeSize.Trim(),
            VolumeUnit = Math.Clamp(source.VolumeUnit, 0, 2),
            RecoveryRecordPercent = Math.Clamp(source.RecoveryRecordPercent, 0, 100),
            ExistingFileMode = Math.Clamp(source.ExistingFileMode, 0, 2),
            LockArchive = source.LockArchive,
            QuickOpen = source.QuickOpen,
            TestArchive = source.TestArchive,
            EnableComment = source.EnableComment,
            CommentFilePath = source.CommentFilePath ?? "注释.txt",
            SkipAlreadyProcessed = source.SkipAlreadyProcessed,
            MoveSourceAfter = source.MoveSourceAfter,
            StrictExtract = source.StrictExtract,
            MaxSizeGB = double.IsFinite(source.MaxSizeGB) && source.MaxSizeGB > 0 ? source.MaxSizeGB : 666,
            AddEnclosures = source.AddEnclosures
        };
    }

    private static OperationTabPreferences ToTabPreferences(TabDto? dto)
    {
        if (dto is null)
        {
            return OperationTabPreferences.CreateDefault();
        }

        return new OperationTabPreferences
        {
            SourceMode = dto.SourceMode,
            Extension = dto.Extension,
            UseRandomPassword = dto.UseRandomPassword,
            PasswordNameMode = dto.PasswordNameMode,
            CompressionLevel = dto.CompressionLevel,
            SolidArchive = dto.SolidArchive,
            EnableVolume = dto.EnableVolume,
            VolumeSize = dto.VolumeSize,
            VolumeUnit = dto.VolumeUnit,
            RecoveryRecordPercent = dto.RecoveryRecordPercent,
            ExistingFileMode = dto.ExistingFileMode,
            LockArchive = dto.LockArchive,
            QuickOpen = dto.QuickOpen,
            TestArchive = dto.TestArchive,
            EnableComment = dto.EnableComment,
            CommentFilePath = dto.CommentFilePath,
            SkipAlreadyProcessed = dto.SkipAlreadyProcessed,
            MoveSourceAfter = dto.MoveSourceAfter,
            StrictExtract = dto.StrictExtract,
            MaxSizeGB = dto.MaxSizeGB,
            AddEnclosures = dto.AddEnclosures
        };
    }

    private static TabDto FromTabPreferences(OperationTabPreferences tab) => new()
    {
        SourceMode = tab.SourceMode,
        Extension = tab.Extension,
        UseRandomPassword = tab.UseRandomPassword,
        PasswordNameMode = tab.PasswordNameMode,
        CompressionLevel = tab.CompressionLevel,
        SolidArchive = tab.SolidArchive,
        EnableVolume = tab.EnableVolume,
        VolumeSize = tab.VolumeSize,
        VolumeUnit = tab.VolumeUnit,
        RecoveryRecordPercent = tab.RecoveryRecordPercent,
        ExistingFileMode = tab.ExistingFileMode,
        LockArchive = tab.LockArchive,
        QuickOpen = tab.QuickOpen,
        TestArchive = tab.TestArchive,
        EnableComment = tab.EnableComment,
        CommentFilePath = tab.CommentFilePath,
        SkipAlreadyProcessed = tab.SkipAlreadyProcessed,
        MoveSourceAfter = tab.MoveSourceAfter,
        StrictExtract = tab.StrictExtract,
        MaxSizeGB = tab.MaxSizeGB,
        AddEnclosures = tab.AddEnclosures
    };

    private sealed class PreferencesDto
    {
        public int SchemaVersion { get; set; } = CurrentSchemaVersion;
        public string? Language { get; set; }
        public int MaxConcurrency { get; set; } = 1;
        public int ActiveOperationTab { get; set; }
        public TabDto? CompressionTab { get; set; }
        public TabDto? DecompressionTab { get; set; }
    }

    private sealed class TabDto
    {
        public int SourceMode { get; set; } = 1;
        public string Extension { get; set; } = "rar";
        public bool UseRandomPassword { get; set; } = true;
        public int PasswordNameMode { get; set; }
        public int CompressionLevel { get; set; } = ArchiveDefaults.DefaultCompressionLevel;
        public bool SolidArchive { get; set; } = true;
        public bool EnableVolume { get; set; } = ArchiveDefaults.DefaultEnableVolume;
        public string VolumeSize { get; set; } = ArchiveDefaults.DefaultVolumeSize;
        public int VolumeUnit { get; set; }
        public int RecoveryRecordPercent { get; set; } = ArchiveDefaults.DefaultRecoveryRecordPercent;
        public int ExistingFileMode { get; set; } = 2;
        public bool LockArchive { get; set; }
        public bool QuickOpen { get; set; }
        public bool TestArchive { get; set; }
        public bool EnableComment { get; set; } = true;
        public string CommentFilePath { get; set; } = "注释.txt";
        public bool SkipAlreadyProcessed { get; set; } = true;
        public bool MoveSourceAfter { get; set; }
        public bool StrictExtract { get; set; }
        public double MaxSizeGB { get; set; } = 666;
        public bool AddEnclosures { get; set; } = true;
    }
}
