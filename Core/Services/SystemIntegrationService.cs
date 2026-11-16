using System;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Input.Platform;
using BatchCompress.Avalonia.Core.Interfaces;

namespace BatchCompress.Avalonia.Core.Services;

/// <summary>跨平台系统集成功能实现。</summary>
// GPT-5, 2026-08-05：将应用动作映射到 macOS、Windows、Linux 原生命令，同时使用 ArgumentList 传递路径和通知文本，避免 Shell 解释。
public class SystemIntegrationService : ISystemIntegration
{
    // WorkBuddy, 2026-11-16：0.8.12——关机实现抽到 SystemPowerService，
    // 与命令行共用；macOS 改用 AppleScript，避免普通用户执行 shutdown 因缺少 root 而失败。
    private readonly SystemPowerService _powerService = new();

    public async Task OpenFolderAsync(string path)
    {
        if (!Directory.Exists(path)) return;

        try
        {
            // GPT-5, 2026-08-05：选择原生文件管理器启动命令，不引入桌面环境专属依赖。
            var command = RuntimeInformation.IsOSPlatform(OSPlatform.Windows) ? "explorer.exe" :
                RuntimeInformation.IsOSPlatform(OSPlatform.OSX) ? "open" : "xdg-open";
            await RunDetachedAsync(command, new[] { path });
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"打开目录失败: {ex.Message}");
        }
    }

    public async Task<string?> ReadClipboardTextAsync()
    {
        try
        {
            if (Application.Current?.ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop &&
                desktop.MainWindow?.Clipboard is { } clipboard)
            {
                // GPT-5, 2026-08-06：Avalonia 12 将读取文本迁移为 ClipboardExtensions.TryGetTextAsync。
                return await clipboard.TryGetTextAsync();
            }
        }
        catch (Exception ex) { Debug.WriteLine($"读取剪贴板失败: {ex.Message}"); }

        return null;
    }

    public async Task WriteClipboardTextAsync(string text)
    {
        try
        {
            if (Application.Current?.ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop &&
                desktop.MainWindow?.Clipboard is { } clipboard)
            {
                // GPT-5, 2026-08-06：写入文本继续使用 Avalonia 12 的 ClipboardExtensions.SetTextAsync。
                await clipboard.SetTextAsync(text);
            }
        }
        catch (Exception ex) { Debug.WriteLine($"写入剪贴板失败: {ex.Message}"); }
    }

    public void ShowNotification(string title, string message)
    {
        try
        {
            // GPT-5, 2026-08-05：各平台使用可用的原生通知桥接；失败不影响批处理主流程。
            if (RuntimeInformation.IsOSPlatform(OSPlatform.OSX))
            {
                var script = $"display notification {AppleScriptString(message)} with title {AppleScriptString(title)}";
                StartDetached("osascript", new[] { "-e", script });
            }
            else if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
            {
                var script = "Add-Type -AssemblyName System.Windows.Forms; " +
                    "$n = New-Object System.Windows.Forms.NotifyIcon; " +
                    "$n.Icon = [System.Drawing.SystemIcons]::Information; " +
                    $"$n.BalloonTipTitle = '{PowerShellString(title)}'; " +
                    $"$n.BalloonTipText = '{PowerShellString(message)}'; " +
                    "$n.Visible = $true; $n.ShowBalloonTip(5000); Start-Sleep -Seconds 1; $n.Dispose()";
                StartDetached("powershell", new[] { "-NoProfile", "-ExecutionPolicy", "Bypass", "-Command", script });
            }
            else if (RuntimeInformation.IsOSPlatform(OSPlatform.Linux))
            {
                StartDetached("notify-send", new[] { title, message });
            }
        }
        catch (Exception ex) { Debug.WriteLine($"显示通知失败: {ex.Message}"); }
    }

    public Task ShutdownAsync() => _powerService.ShutdownAsync();

    public async Task CancelShutdownAsync()
    {
        if (!await _powerService.CancelShutdownAsync())
        {
            ShowNotification("操作失败", "取消关机失败，可能没有待取消的关机请求。");
        }
    }

    private static async Task RunDetachedAsync(string fileName, string[] arguments)
    {
        using var process = StartProcess(fileName, arguments);
        await Task.CompletedTask;
    }

    private static void StartDetached(string fileName, string[] arguments)
    {
        _ = StartProcess(fileName, arguments);
    }

    private static Process StartProcess(string fileName, string[] arguments)
    {
        // GPT-5, 2026-08-05：使用 ArgumentList 保留所有文件路径和用户文本的参数边界。
        var info = new ProcessStartInfo { FileName = fileName, UseShellExecute = false, CreateNoWindow = true };
        foreach (var argument in arguments) info.ArgumentList.Add(argument);
        return Process.Start(info) ?? throw new InvalidOperationException($"无法启动 {fileName}");
    }

    private static string AppleScriptString(string value) =>
        $"\"{value.Replace("\\", "\\\\").Replace("\"", "\\\"").Replace("\r", " ").Replace("\n", " ")}\"";

    private static string PowerShellString(string value) => value.Replace("'", "''").Replace("\r", " ").Replace("\n", " ");
}
