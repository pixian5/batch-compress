using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Threading.Tasks;

namespace BatchCompress.Avalonia.Core.Services;

/// <summary>一条可执行命令及其参数；参数逐个传递，不拼接 Shell 字符串。</summary>
public sealed record SystemCommand(string FileName, IReadOnlyList<string> Arguments);

// WorkBuddy, 2026-11-16：0.8.12——用户决定“命令行的自动关机开关要真的关机”。
// 此前命令行侧只写一行日志、不执行任何系统命令，而帮助里承诺会关机。
// 这里把关机/取消关机抽成不依赖界面框架的服务，命令行与图形界面共用同一套实现。
public sealed class SystemPowerService
{
    private readonly object _gate = new();
    private Process? _pendingMacShutdown;

    /// <summary>按当前操作系统构造关机命令；保留一分钟延迟，给用户取消机会。</summary>
    public static SystemCommand BuildShutdownCommand() => BuildShutdownCommand(ResolvePlatform());

    internal static SystemCommand BuildShutdownCommand(OSPlatform platform)
    {
        if (platform == OSPlatform.Windows)
        {
            return new SystemCommand("shutdown", ["/s", "/t", "60"]);
        }

        if (platform == OSPlatform.OSX)
        {
            // WorkBuddy, 2026-11-16：macOS 的 /sbin/shutdown 需要 root，普通用户会直接失败。
            // AppleScript 以当前用户身份请求系统关机；把 delay 放进脚本自身，
            // 既保留 60 秒取消窗口，又不需要提前结束进程。
            return new SystemCommand(
                "/usr/bin/osascript",
                ["-e", "delay 60", "-e", "tell application \"System Events\" to shut down"]);
        }

        // Linux：shutdown 通过 logind 提交，一分钟延迟，取消用 shutdown -c。
        return new SystemCommand("shutdown", ["-h", "+1"]);
    }

    internal static SystemCommand BuildCancelCommand(OSPlatform platform)
    {
        if (platform == OSPlatform.Windows)
        {
            return new SystemCommand("shutdown", ["/a"]);
        }

        return new SystemCommand("shutdown", ["-c"]);
    }

    /// <summary>提交关机请求；返回 true 表示系统命令已被接受。</summary>
    public async Task<bool> ShutdownAsync()
    {
        if (OperatingSystem.IsMacOS())
        {
            return ScheduleMacShutdown();
        }

        var command = BuildShutdownCommand(ResolvePlatform());
        return await RunAndWaitAsync(command, "启动关机").ConfigureAwait(false);
    }

    /// <summary>取消已提交的关机请求；没有待取消的请求时返回 false。</summary>
    public async Task<bool> CancelShutdownAsync()
    {
        if (OperatingSystem.IsMacOS())
        {
            lock (_gate)
            {
                if (_pendingMacShutdown is not { } process)
                {
                    return false;
                }

                try
                {
                    if (!process.HasExited)
                    {
                        process.Kill();
                    }
                }
                catch (Exception ex)
                {
                    Debug.WriteLine($"取消 macOS 关机失败: {ex.Message}");
                    return false;
                }
                finally
                {
                    process.Dispose();
                    _pendingMacShutdown = null;
                }

                return true;
            }
        }

        return await RunAndWaitAsync(BuildCancelCommand(ResolvePlatform()), "取消关机").ConfigureAwait(false);
    }

    private bool ScheduleMacShutdown()
    {
        var command = BuildShutdownCommand(OSPlatform.OSX);
        try
        {
            lock (_gate)
            {
                if (_pendingMacShutdown is { HasExited: false } previous)
                {
                    try
                    {
                        previous.Kill();
                    }
                    catch
                    {
                        // 旧的延迟进程已经退出或无法结束，直接换新的请求。
                    }
                }

                _pendingMacShutdown?.Dispose();
                _pendingMacShutdown = StartProcess(command);
            }

            return true;
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"启动 macOS 关机失败: {ex.Message}");
            return false;
        }
    }

    private static async Task<bool> RunAndWaitAsync(SystemCommand command, string operation)
    {
        try
        {
            using var process = StartProcess(command);
            await process.WaitForExitAsync().ConfigureAwait(false);
            if (process.ExitCode == 0)
            {
                return true;
            }

            Debug.WriteLine($"{operation}失败，退出码 {process.ExitCode}");
            return false;
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"{operation}失败: {ex.Message}");
            return false;
        }
    }

    private static Process StartProcess(SystemCommand command)
    {
        // 参数逐个加入 ArgumentList，路径和脚本文本不会被 Shell 解释。
        var info = new ProcessStartInfo
        {
            FileName = command.FileName,
            UseShellExecute = false,
            CreateNoWindow = true
        };
        foreach (var argument in command.Arguments)
        {
            info.ArgumentList.Add(argument);
        }

        return Process.Start(info) ?? throw new InvalidOperationException($"无法启动 {command.FileName}");
    }

    private static OSPlatform ResolvePlatform()
    {
        if (OperatingSystem.IsWindows())
        {
            return OSPlatform.Windows;
        }

        return OperatingSystem.IsMacOS() ? OSPlatform.OSX : OSPlatform.Linux;
    }
}
