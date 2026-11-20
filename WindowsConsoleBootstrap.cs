using System;
using System.IO;
using System.Runtime.InteropServices;

namespace BatchCompress.Avalonia;

/// <summary>仅在 Windows 命令行启动时连接父进程控制台，不影响双击启动的图形界面。</summary>
internal static class WindowsConsoleBootstrap
{
    private const uint AttachParentProcess = 0xFFFFFFFF;

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool AttachConsole(uint processId);

    public static bool ShouldAttachToParentConsole(string[] args)
    {
        if (args.Length == 0)
        {
            return false;
        }

        foreach (var argument in args)
        {
            if (argument is "--help" or "-h" or "-?" or "/?" or "--version" or
                "compress" or "extract" or "decompress" or "--compress" or "-c" or
                "--decompress" or "-d" or "--export-diagnostics" or "--input" or
                "--source" or "--output" or "--text-file")
            {
                return true;
            }
        }

        return false;
    }

    public static void AttachForCommandLine(string[] args)
    {
        if (!OperatingSystem.IsWindows() || !ShouldAttachToParentConsole(args))
        {
            return;
        }

        try
        {
            if (!AttachConsole(AttachParentProcess))
            {
                // 父进程可能没有控制台，或已通过启动句柄提供标准流；沿用现有句柄即可。
                return;
            }

            // GUI 子系统进程在连接控制台后显式重开标准流，确保 Console.Write* 使用新句柄。
            Console.SetIn(new StreamReader(Console.OpenStandardInput(), Console.InputEncoding));
            Console.SetOut(new StreamWriter(Console.OpenStandardOutput(), Console.OutputEncoding) { AutoFlush = true });
            Console.SetError(new StreamWriter(Console.OpenStandardError(), Console.OutputEncoding) { AutoFlush = true });
        }
        catch (Exception ex) when (ex is IOException or InvalidOperationException or UnauthorizedAccessException)
        {
            // 控制台连接只是 CLI 输出通道；失败时让既有日志与退出码路径继续工作。
            _ = ex;
        }
    }
}
