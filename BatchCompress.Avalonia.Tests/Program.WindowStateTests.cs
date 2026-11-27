using System;
using System.IO;
using BatchCompress.Avalonia.Core.Services;

// WorkBuddy, 2026-11-16：批次 25（P2）——窗口状态持久化。
// 与 Program.cs 保持一致：测试工程使用全局命名空间。
internal static partial class Program
{
    private static Task TestWindowStateService()
    {
        var directory = Path.Combine(Path.GetTempPath(), "bc-window-" + Guid.NewGuid().ToString("N"));
        var settingsPath = Path.Combine(directory, "nested", "window-settings.json");

        try
        {
            var service = new WindowStateService(settingsPath);

            // 1. 文件不存在时返回 null，不能抛异常。
            Assert(service.Load() is null, "设置文件不存在时应返回 null");

            // 2. 保存后读回，尺寸、位置与最大化状态都要一致（含自动建目录）。
            service.Save(new WindowStateSnapshot(1280, 800, 40, 60, IsMaximized: true));
            Assert(File.Exists(settingsPath), "保存后应自动创建目录与文件");

            var loaded = service.Load();
            Assert(loaded is not null, "保存后应能读回");
            AssertEqual(1280d, loaded!.Width);
            AssertEqual(800d, loaded.Height);
            AssertEqual(40, loaded.X);
            AssertEqual(60, loaded.Y);
            Assert(loaded.IsMaximized, "最大化状态应保持");

            // 3. 与旧版本 JSON 格式兼容：WindowState 用 2 表示最大化（Avalonia 数值）。
            File.WriteAllText(settingsPath,
                "{\"Width\":900,\"Height\":700,\"X\":12,\"Y\":24,\"WindowState\":2}");
            var legacy = service.Load();
            Assert(legacy is not null && legacy.IsMaximized, "旧格式的最大化状态应被识别");
            AssertEqual(900d, legacy!.Width);

            File.WriteAllText(settingsPath,
                "{\"Width\":900,\"Height\":700,\"X\":null,\"Y\":null,\"WindowState\":0}");
            var normal = service.Load();
            Assert(normal is not null && !normal.IsMaximized, "旧格式的普通状态应被识别");
            Assert(normal!.X is null && normal.Y is null, "缺失的位置应保持 null");

            // 4. 损坏的 JSON 返回 null，绝不阻止窗口打开。
            File.WriteAllText(settingsPath, "{ not-json");
            Assert(service.Load() is null, "损坏的设置文件应返回 null");
        }
        finally
        {
            try
            {
                if (Directory.Exists(directory)) Directory.Delete(directory, recursive: true);
            }
            catch
            {
                // 清理失败不影响结论。
            }
        }

        return Task.CompletedTask;
    }

    // Gemini 4 Neon, 2026-11-27：批次 40（v0.8.29）——安全 GUI 用户偏好持久化、边界钳制、损坏容错及接线守护。
    private static Task TestUserPreferencesService()
    {
        var directory = Path.Combine(Path.GetTempPath(), "bc-prefs-" + Guid.NewGuid().ToString("N"));
        var prefsPath = Path.Combine(directory, "nested", "user-preferences.json");

        try
        {
            var service = new UserPreferencesService(prefsPath);

            // 1. 文件不存在返回 null。
            Assert(service.Load() is null, "偏好文件不存在时应返回 null");

            // 2. 完整往返（语言、并发度、页签、压缩/解压页各自参数）。
            var expected = new UserPreferencesSnapshot
            {
                Language = "en",
                MaxConcurrency = 4,
                ActiveOperationTab = 1,
                CompressionTab = new OperationTabPreferences
                {
                    SourceMode = 0,
                    Extension = "7z",
                    UseRandomPassword = false,
                    PasswordNameMode = 1,
                    CompressionLevel = 5,
                    SolidArchive = true,
                    EnableVolume = true,
                    VolumeSize = "2",
                    VolumeUnit = 1,
                    RecoveryRecordPercent = 10,
                    ExistingFileMode = 1,
                    LockArchive = true,
                    QuickOpen = true,
                    TestArchive = true,
                    EnableComment = false,
                    CommentFilePath = "custom-note.txt",
                    SkipAlreadyProcessed = false,
                    MoveSourceAfter = true,
                    StrictExtract = false,
                    MaxSizeGB = 128.5,
                    AddEnclosures = false
                },
                DecompressionTab = new OperationTabPreferences
                {
                    SourceMode = 1,
                    Extension = "rar",
                    UseRandomPassword = true,
                    PasswordNameMode = 0,
                    CompressionLevel = 3,
                    SolidArchive = false,
                    EnableVolume = false,
                    VolumeSize = "4",
                    VolumeUnit = 0,
                    RecoveryRecordPercent = 3,
                    ExistingFileMode = 0,
                    LockArchive = false,
                    QuickOpen = false,
                    TestArchive = true,
                    EnableComment = true,
                    CommentFilePath = "",
                    SkipAlreadyProcessed = true,
                    MoveSourceAfter = true,
                    StrictExtract = true,
                    MaxSizeGB = 50,
                    AddEnclosures = true
                }
            };

            service.Save(expected);
            Assert(File.Exists(prefsPath), "保存用户偏好后应创建目标文件");

            // 原子写入不应留下任何 .tmp 文件。
            var tmpFiles = Directory.GetFiles(Path.GetDirectoryName(prefsPath)!, "*.tmp");
            AssertEqual(0, tmpFiles.Length);

            // 安全检查：序列化出的 JSON 绝不能包含明文密码、删除源或完成后关机字段。
            var rawJson = File.ReadAllText(prefsPath);
            Assert(!rawJson.Contains("CustomPassword", StringComparison.OrdinalIgnoreCase),
                "偏好文件不得持久化 CustomPassword");
            Assert(!rawJson.Contains("DeleteSourceAfter", StringComparison.OrdinalIgnoreCase),
                "偏好文件不得持久化 DeleteSourceAfter");
            Assert(!rawJson.Contains("ShutdownAfterComplete", StringComparison.OrdinalIgnoreCase),
                "偏好文件不得持久化 ShutdownAfterComplete");

            var loaded = service.Load();
            Assert(loaded is not null, "保存后应能正常读回用户偏好");
            AssertEqual("en", loaded!.Language);
            AssertEqual(4, loaded.MaxConcurrency);
            AssertEqual(1, loaded.ActiveOperationTab);
            AssertEqual("7z", loaded.CompressionTab.Extension);
            AssertEqual(5, loaded.CompressionTab.CompressionLevel);
            AssertEqual(true, loaded.CompressionTab.EnableVolume);
            AssertEqual("2", loaded.CompressionTab.VolumeSize);
            AssertEqual(1, loaded.CompressionTab.VolumeUnit);
            AssertEqual(10, loaded.CompressionTab.RecoveryRecordPercent);
            AssertEqual("custom-note.txt", loaded.CompressionTab.CommentFilePath);
            AssertEqual(128.5d, loaded.CompressionTab.MaxSizeGB);
            AssertEqual(false, loaded.CompressionTab.AddEnclosures);
            AssertEqual(true, loaded.DecompressionTab.StrictExtract);
            AssertEqual(true, loaded.DecompressionTab.MoveSourceAfter);
            AssertEqual(string.Empty, loaded.DecompressionTab.CommentFilePath);

            // 3. 边界值与异常值归一化。
            service.Save(new UserPreferencesSnapshot
            {
                Language = "   ",
                MaxConcurrency = 99,
                ActiveOperationTab = -5,
                CompressionTab = new OperationTabPreferences
                {
                    SourceMode = 9,
                    Extension = "  ",
                    PasswordNameMode = -2,
                    CompressionLevel = 0,
                    SolidArchive = true,
                    VolumeSize = "",
                    VolumeUnit = 8,
                    RecoveryRecordPercent = 200,
                    ExistingFileMode = -1,
                    MaxSizeGB = double.NaN
                },
                DecompressionTab = new OperationTabPreferences
                {
                    MaxSizeGB = -10
                }
            });
            var clamped = service.Load();
            Assert(clamped is not null, "归一化后应能读回");
            Assert(clamped!.Language is null, "空白语言应归一为 null");
            AssertEqual(16, clamped.MaxConcurrency);
            AssertEqual(0, clamped.ActiveOperationTab);
            AssertEqual(1, clamped.CompressionTab.SourceMode);
            AssertEqual("rar", clamped.CompressionTab.Extension);
            AssertEqual(0, clamped.CompressionTab.PasswordNameMode);
            AssertEqual(0, clamped.CompressionTab.CompressionLevel);
            Assert(!clamped.CompressionTab.SolidArchive, "压缩级别为 0 时 SolidArchive 必须关闭");
            AssertEqual(BatchCompress.Avalonia.Core.Models.ArchiveDefaults.DefaultVolumeSize, clamped.CompressionTab.VolumeSize);
            AssertEqual(2, clamped.CompressionTab.VolumeUnit);
            AssertEqual(100, clamped.CompressionTab.RecoveryRecordPercent);
            AssertEqual(0, clamped.CompressionTab.ExistingFileMode);
            AssertEqual(666d, clamped.CompressionTab.MaxSizeGB);
            AssertEqual(666d, clamped.DecompressionTab.MaxSizeGB);

            // 4. 手工注入敏感字段也不影响正常反序列化（会被忽略）。
            File.WriteAllText(prefsPath,
                """
                {
                  "SchemaVersion": 1,
                  "Language": "ja",
                  "MaxConcurrency": 0,
                  "CustomPassword": "leak",
                  "DeleteSourceAfter": true,
                  "ShutdownAfterComplete": true
                }
                """);
            var injected = service.Load();
            Assert(injected is not null, "包含额外未知字段时应正常读取安全字段");
            AssertEqual("ja", injected!.Language);
            AssertEqual(1, injected.MaxConcurrency);

            // 5. 损坏的 JSON 返回 null。
            File.WriteAllText(prefsPath, "{ corrupt-json");
            Assert(service.Load() is null, "损坏的用户偏好文件应安全返回 null");

            // 6. 源码接线守护：MainWindowViewModel 与 Lifecycle 必须正确加载/保存且尊重 --lang。
            var root = FindRepositoryRoot();
            var vmText = File.ReadAllText(Path.Combine(root, "ViewModels", "MainWindowViewModel.cs"));
            Assert(vmText.Contains("UserPreferencesService", StringComparison.Ordinal),
                "MainWindowViewModel 缺少 UserPreferencesService");
            Assert(vmText.Contains("ApplySavedPreferences", StringComparison.Ordinal),
                "MainWindowViewModel 缺少 ApplySavedPreferences");
            Assert(vmText.Contains("SaveUserPreferences", StringComparison.Ordinal),
                "MainWindowViewModel 缺少 SaveUserPreferences");
            Assert(vmText.Contains("LocalizationService.Instance.LanguageExplicitlySet", StringComparison.Ordinal),
                "MainWindowViewModel 必须保留 --lang 覆盖优先判断");

            var lifecycleText = File.ReadAllText(Path.Combine(root, "ViewModels", "MainWindowViewModel.Lifecycle.cs"));
            Assert(lifecycleText.Contains("SaveUserPreferences();", StringComparison.Ordinal),
                "MainWindowViewModel.Dispose 未保存用户偏好");
        }
        finally
        {
            try
            {
                if (Directory.Exists(directory)) Directory.Delete(directory, recursive: true);
            }
            catch
            {
                // 清理失败不影响结论。
            }
        }

        return Task.CompletedTask;
    }
}
