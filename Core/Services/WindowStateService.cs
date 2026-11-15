using System;
using System.IO;
using System.Text.Json;

namespace BatchCompress.Avalonia.Core.Services;

/// <summary>窗口尺寸、位置与最大化状态的快照。</summary>
public sealed record WindowStateSnapshot(
    double Width,
    double Height,
    int? X,
    int? Y,
    bool IsMaximized);

/// <summary>
/// 窗口状态持久化。
/// WorkBuddy, 2026-11-16：批次 25（P2）——从 MainWindow 代码隐藏抽离，视图只负责采集尺寸与位置。
/// 文件路径与 JSON 字段保持不变（Width/Height/X/Y/WindowState），其中 WindowState 用 0/2
/// 与 Avalonia 的 Normal/Maximized 数值对齐，升级后不会丢失用户已保存的窗口位置。
/// 本类刻意不引用 Avalonia 类型，便于单独测试。
/// </summary>
public sealed class WindowStateService
{
    private static readonly JsonSerializerOptions SerializerOptions = new();

    public WindowStateService(string? settingsPath = null)
    {
        SettingsPath = settingsPath ?? DefaultSettingsPath;
    }

    public string SettingsPath { get; }

    /// <summary>各平台正确的用户数据目录下的设置文件路径。</summary>
    public static string DefaultSettingsPath
    {
        get
        {
            // ApplicationData 在 Windows 对应 AppData、macOS 对应 Application Support、Linux 对应用户数据目录。
            var applicationData = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
            if (string.IsNullOrWhiteSpace(applicationData))
            {
                applicationData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
            }

            return Path.Combine(
                string.IsNullOrWhiteSpace(applicationData) ? AppContext.BaseDirectory : applicationData,
                "BatchCompress.Avalonia",
                "window-settings.json");
        }
    }

    /// <summary>读取失败或文件不存在时返回 null，绝不阻止主窗口打开。</summary>
    public WindowStateSnapshot? Load()
    {
        try
        {
            if (!File.Exists(SettingsPath))
            {
                return null;
            }

            var dto = JsonSerializer.Deserialize<SettingsDto>(
                File.ReadAllText(SettingsPath), SerializerOptions);
            if (dto is null)
            {
                return null;
            }

            return new WindowStateSnapshot(
                dto.Width,
                dto.Height,
                dto.X,
                dto.Y,
                dto.WindowState == MaximizedValue);
        }
        catch
        {
            return null;
        }
    }

    /// <summary>持久化是尽力而为的操作，失败不影响应用退出。</summary>
    public void Save(WindowStateSnapshot snapshot)
    {
        ArgumentNullException.ThrowIfNull(snapshot);

        try
        {
            var directory = Path.GetDirectoryName(SettingsPath);
            if (!string.IsNullOrEmpty(directory))
            {
                Directory.CreateDirectory(directory);
            }

            var dto = new SettingsDto
            {
                Width = snapshot.Width,
                Height = snapshot.Height,
                X = snapshot.X,
                Y = snapshot.Y,
                WindowState = snapshot.IsMaximized ? MaximizedValue : NormalValue
            };

            File.WriteAllText(SettingsPath, JsonSerializer.Serialize(dto, SerializerOptions));
        }
        catch
        {
            // 窗口状态持久化失败不能阻塞关闭或退出。
        }
    }

    // Avalonia 的 WindowState.Normal = 0、Maximized = 2；保持与历史文件一致。
    private const int NormalValue = 0;
    private const int MaximizedValue = 2;

    private sealed class SettingsDto
    {
        public double Width { get; set; }
        public double Height { get; set; }
        public int? X { get; set; }
        public int? Y { get; set; }
        public int WindowState { get; set; }
    }
}
