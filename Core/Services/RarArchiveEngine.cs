using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using BatchCompress.Avalonia.Core.Interfaces;

namespace BatchCompress.Avalonia.Core.Services;

/// <summary>
/// RAR 压缩引擎实现
/// 跨平台支持 RAR/UnRAR
/// </summary>
// GPT-5, 2026-08-05：按平台定位并验证唯一的 WinRAR/RAR 可执行文件，再交由 WinRarProcessRunner 执行归档操作。
// 不在此处拼接 Shell 命令字符串，所有参数边界由参数列表保证。
public class RarArchiveEngine : IArchiveEngine
{
    private string? _rarExecutablePath;
    
    public bool IsAvailable() =>
        IsAvailableAsync(CancellationToken.None).GetAwaiter().GetResult();

    public async Task<bool> IsAvailableAsync(CancellationToken cancellationToken = default)
    {
        _rarExecutablePath = await FindRarExecutableAsync(cancellationToken).ConfigureAwait(false);
        return !string.IsNullOrEmpty(_rarExecutablePath);
    }

    private async Task<string?> FindRarExecutableAsync(CancellationToken cancellationToken)
    {
        var candidates = new List<string>();
        await foreach (var candidate in GetCandidatePathsAsync(cancellationToken).ConfigureAwait(false))
        {
            candidates.Add(candidate);
        }

        Debug.WriteLine($"[RAR] 操作系统={RuntimeInformation.OSDescription}");
        Debug.WriteLine($"[RAR] 程序目录={AppContext.BaseDirectory}");
        Debug.WriteLine($"[RAR] 候选路径({candidates.Count}): {string.Join(" | ", candidates)}");

        foreach (var candidate in candidates)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var normalized = NormalizeCandidate(candidate);
            if (string.IsNullOrWhiteSpace(normalized))
            {
                continue;
            }

            var (isValid, validationMessage) = await TryValidateRarExecutableAsync(normalized, cancellationToken).ConfigureAwait(false);
            if (isValid)
            {
                Debug.WriteLine($"[RAR] 已选路径: {normalized}");
                return normalized;
            }

            if (!string.IsNullOrWhiteSpace(validationMessage))
            {
                Debug.WriteLine($"[RAR] 拒绝: {normalized}（{validationMessage}）");
            }
        }

        Debug.WriteLine("[RAR] 未找到可用的 RAR 程序");
        return null;
    }
    

    private async IAsyncEnumerable<string> GetCandidatePathsAsync(
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        // 允许部署者显式提供其自有授权的完整 RAR 可执行文件；该路径优先于所有平台默认位置。
        var configuredPath = Environment.GetEnvironmentVariable("BATCHCOMPRESS_RAR_PATH");
        if (!string.IsNullOrWhiteSpace(configuredPath))
        {
            yield return configuredPath;
        }

        if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
        {
            foreach (var p in GetWindowsCandidates())
            {
                yield return p;
            }
            yield break;
        }

        if (RuntimeInformation.IsOSPlatform(OSPlatform.OSX))
        {
            await foreach (var p in GetMacCandidatesAsync(cancellationToken).ConfigureAwait(false))
            {
                yield return p;
            }
            yield break;
        }

        if (RuntimeInformation.IsOSPlatform(OSPlatform.Linux))
        {
            await foreach (var p in GetLinuxCandidatesAsync(cancellationToken).ConfigureAwait(false))
            {
                yield return p;
            }
            yield break;
        }
    }

    private IEnumerable<string> GetWindowsCandidates()
    {
        // 1) 程序运行目录下的 tools/WinRAR 目录（优先使用）
        var toolsWinrarDir = Path.Combine(AppContext.BaseDirectory, "tools", "WinRAR");
        yield return Path.Combine(toolsWinrarDir, "Rar.exe");
        yield return Path.Combine(toolsWinrarDir, "WinRAR.exe");

        // 2) 注册表 App Paths（HKLM/HKCU）
        foreach (var p in EnumerateAppPathsFromRegistry("rar.exe"))
        {
            yield return p;
        }
        foreach (var p in EnumerateAppPathsFromRegistry("winrar.exe"))
        {
            yield return p;
        }

        // 3) 默认安装目录
        yield return @"C:\Program Files\WinRAR\rar.exe";
        yield return @"C:\Program Files\WinRAR\winrar.exe";
        yield return @"C:\Program Files (x86)\WinRAR\rar.exe";
        yield return @"C:\Program Files (x86)\WinRAR\winrar.exe";
        
        // 4) 程序运行目录下的 winrar 目录（向后兼容，最后使用）
        var winrarDir = Path.Combine(AppContext.BaseDirectory, "winrar");
        yield return Path.Combine(winrarDir, "rar.exe");
        yield return Path.Combine(winrarDir, "winrar.exe");
    }

    private IEnumerable<string> EnumerateAppPathsFromRegistry(string exeName)
    {
        if (!OperatingSystem.IsWindows())
        {
            yield break;
        }

        string subKey = $@"SOFTWARE\Microsoft\Windows\CurrentVersion\App Paths\{exeName}";
        var hives = new[]
        {
            Microsoft.Win32.RegistryHive.LocalMachine,
            Microsoft.Win32.RegistryHive.CurrentUser
        };

        foreach (var hive in hives)
        {
            string? defaultValue = null;
            string? pathValue = null;

            try
            {
                using var baseKey = Microsoft.Win32.RegistryKey.OpenBaseKey(hive, Microsoft.Win32.RegistryView.Registry64);
                using var key = baseKey.OpenSubKey(subKey);
                if (key == null)
                {
                    continue;
                }

                // 默认值可能是完整路径
                defaultValue = key.GetValue("")?.ToString();

                // Path 值可能是目录
                pathValue = key.GetValue("Path")?.ToString();
            }
            catch
            {
                // 当前候选路径不可用时继续检查下一个候选路径。
            }

            if (!string.IsNullOrWhiteSpace(defaultValue))
            {
                yield return defaultValue;
            }

            if (!string.IsNullOrWhiteSpace(pathValue))
            {
                yield return Path.Combine(pathValue, exeName);
            }
        }
    }

    private async IAsyncEnumerable<string> GetMacCandidatesAsync(
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        // GPT-5, 2026-08-05：优先使用应用包内的 RAR；Finder 启动的进程可能不会继承用户 PATH。
        var toolsRarDir = Path.Combine(AppContext.BaseDirectory, "tools", "rarmacOS", "rar");
        yield return toolsRarDir;

        // GPT-5, 2026-08-05：在查询 PATH 前先检查常见包管理器目录，保证 macOS 查找顺序确定。
        yield return "/opt/homebrew/bin/rar";
        yield return "/usr/local/bin/rar";
        yield return "/usr/bin/rar";

        // GPT-5, 2026-08-05：仅将 which 作为后备方案，因为其结果依赖当前进程环境。
        var whichPath = await ResolveWhichRarPathAsync(cancellationToken).ConfigureAwait(false);
        if (!string.IsNullOrWhiteSpace(whichPath))
        {
            yield return whichPath;
        }

        // GPT-5, 2026-08-05：保留历史用户目录安装位置，作为最后的兼容性后备方案。
        yield return ExpandHome("~/rar/rar");
        yield return ExpandHome("~/.local/bin/rar");
        
        // GPT-5, 2026-08-05：保留旧版可执行文件位置，兼容未打包部署。
        yield return Path.Combine(AppContext.BaseDirectory, "rar");
    }

    private async IAsyncEnumerable<string> GetLinuxCandidatesAsync(
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        // 1) 程序运行目录下的 tools/rarLinux 目录（优先使用）。
        // GLM-5.3, 2026-11-04：批次 3 决策 B——该目录现携带官方完整未修改分发包
        // （rar/unrar/license.txt/readme.txt 等，来源 rarlinux-x64-723.tar.gz），
        // csproj 在 Linux 构建下整体复制，候选路径与实际构建输出一致。
        var toolsRarDir = Path.Combine(AppContext.BaseDirectory, "tools", "rarLinux", "rar");
        yield return toolsRarDir;

        // 2) 固定路径
        yield return "/usr/bin/rar";
        yield return "/usr/local/bin/rar";
        yield return "/bin/rar";

        // 3) PATH/which
        var whichPath = await ResolveWhichRarPathAsync(cancellationToken).ConfigureAwait(false);
        if (!string.IsNullOrWhiteSpace(whichPath))
        {
            yield return whichPath;
        }

        // 4) 用户目录兜底
        yield return ExpandHome("~/rar/rar");
        yield return ExpandHome("~/.local/bin/rar");
        
        // 5) 程序运行目录：rarlinux（向后兼容，最后使用）
        yield return Path.Combine(AppContext.BaseDirectory, "rarlinux");
    }

    private static async Task<string?> ResolveWhichRarPathAsync(CancellationToken cancellationToken)
    {
        var which = await ExecuteCommandCaptureAllAsync("which", ["rar"], timeoutMs: 2000, cancellationToken)
            .ConfigureAwait(false);
        if (string.IsNullOrWhiteSpace(which.Output))
        {
            return null;
        }

        var firstLine = which.Output
            .Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries)
            .FirstOrDefault();
        return string.IsNullOrWhiteSpace(firstLine) ? null : firstLine.Trim();
    }

    private static string ExpandHome(string path)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return path;
        }

        if (!path.StartsWith("~/", StringComparison.Ordinal))
        {
            return path;
        }

        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        if (string.IsNullOrWhiteSpace(home))
        {
            return path;
        }

        return Path.Combine(home, path.Substring(2));
    }

    private static string? NormalizeCandidate(string candidate)
    {
        if (string.IsNullOrWhiteSpace(candidate))
        {
            return null;
        }

        var trimmed = candidate.Trim();
        if (trimmed.StartsWith('"') && trimmed.EndsWith('"') && trimmed.Length >= 2)
        {
            trimmed = trimmed.Substring(1, trimmed.Length - 2);
        }
        return trimmed;
    }

    private static async Task<(bool IsValid, string Message)> TryValidateRarExecutableAsync(
        string path,
        CancellationToken cancellationToken)
    {
        try
        {
            if (!File.Exists(path))
            {
                return (false, "文件不存在");
            }

            if ((File.GetAttributes(path) & FileAttributes.Directory) != 0)
            {
                return (false, "不是文件");
            }

            if (OperatingSystem.IsWindows())
            {
                var ext = Path.GetExtension(path);
                if (!ext.Equals(".exe", StringComparison.OrdinalIgnoreCase))
                {
                    return (false, "非 exe 文件");
                }
            }
            else
            {
                // macOS/Linux：检查执行位；若是程序目录内的文件，允许尝试自动修复权限
                if (!IsUnixExecutable(path))
                {
                    if (IsUnderBaseDirectory(path) && TryFixUnixExecutablePermission(path))
                    {
                        // 修复执行权限后重试一次。
                    }
                    else
                    {
                        return (false, "不可执行（建议 chmod +x）");
                    }
                }
            }

            // 运行校验：rar -?，2 秒
            var result = await ExecuteCommandCaptureAllAsync(path, ["-?"], timeoutMs: 2000, cancellationToken).ConfigureAwait(false);
            if (!result.Started)
            {
                return (false, "无法启动进程");
            }

            if (result.TimedOut)
            {
                return (false, "运行校验超时");
            }

            if (result.ExitCode != 0 && result.ExitCode != 1)
            {
                return (false, $"运行校验返回码异常：{result.ExitCode}");
            }

            var output = (result.Output + "\n" + result.Error).Trim();
            if (string.IsNullOrWhiteSpace(output))
            {
                return (false, "运行校验无输出");
            }

            var lower = output.ToLowerInvariant();
            bool ok = lower.Contains("rar") || lower.Contains("winrar") || lower.Contains("copyright") || lower.Contains("usage");
            if (!ok)
            {
                return (false, "运行校验输出不匹配");
            }

            return (true, string.Empty);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            return (false, $"校验异常：{ex.Message}");
        }
    }

    private static bool IsUnderBaseDirectory(string path)
    {
        try
        {
            var baseDir = Path.GetFullPath(AppContext.BaseDirectory);
            var full = Path.GetFullPath(path);
            return full.StartsWith(baseDir, StringComparison.OrdinalIgnoreCase);
        }
        catch
        {
            return false;
        }
    }

    private static bool IsUnixExecutable(string path)
    {
        try
        {
            if (OperatingSystem.IsWindows())
            {
                return true;
            }

            var mode = File.GetUnixFileMode(path);
            return (mode & (UnixFileMode.UserExecute | UnixFileMode.GroupExecute | UnixFileMode.OtherExecute)) != 0;
        }
        catch
        {
            return false;
        }
    }

    private static bool TryFixUnixExecutablePermission(string path)
    {
        try
        {
            if (OperatingSystem.IsWindows())
            {
                return false;
            }

            var mode = File.GetUnixFileMode(path);
            var newMode = mode | UnixFileMode.UserExecute | UnixFileMode.GroupExecute | UnixFileMode.OtherExecute;
            if (newMode == mode)
            {
                return true;
            }

            File.SetUnixFileMode(path, newMode);
            return true;
        }
        catch
        {
            return false;
        }
    }

    private sealed class CommandResult
    {
        public bool Started { get; init; }
        public bool TimedOut { get; init; }
        public int ExitCode { get; init; }
        public string Output { get; init; } = string.Empty;
        public string Error { get; init; } = string.Empty;
    }

    private static async Task<CommandResult> ExecuteCommandCaptureAllAsync(
        string fileName,
        IReadOnlyList<string> arguments,
        int timeoutMs,
        CancellationToken cancellationToken)
    {
        try
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(timeoutMs);
            var result = await new WinRarProcessRunner(fileName)
                .RunAsync(arguments, timeout.Token)
                .ConfigureAwait(false);

            return new CommandResult
            {
                Started = true,
                ExitCode = result.ExitCode,
                Output = result.StandardOutput,
                Error = result.StandardError
            };
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (OperationCanceledException)
        {
            return new CommandResult { Started = true, TimedOut = true };
        }
        catch (Exception ex)
        {
            return new CommandResult
            {
                Started = false,
                ExitCode = -1,
                Error = ex.Message
            };
        }
    }
    
    /// <summary>
    /// 确保 RAR 可执行文件可用。
    /// </summary>
    private async Task<bool> EnsureRarExecutableAvailableAsync(CancellationToken cancellationToken)
    {
        if (string.IsNullOrEmpty(_rarExecutablePath))
        {
            return await IsAvailableAsync(cancellationToken).ConfigureAwait(false);
        }
        return true;
    }
    
    // Antigravity, 2026-11-08：批次 15（P2-10）——针对 Linux ARM64（自带 RAR 仅针对 Linux x64 提供）
    // 输出明确的架构诊断提示，引导使用系统 rar 或切换为 7z/zip 格式。
    internal static string GetMissingRarMessage()
    {
        if (RuntimeInformation.IsOSPlatform(OSPlatform.Linux) &&
            RuntimeInformation.ProcessArchitecture == Architecture.Arm64)
        {
            return "RAR executable not found on Linux ARM64 (bundled RAR binary is provided for Linux x64 only). Please install system rar or use 7z/zip format.";
        }

        return "RAR executable not found. Please install WinRAR or RAR.";
    }

    public async Task<ArchiveResult> CompressAsync(string input, string output, ArchiveOptions options, CancellationToken cancellationToken = default)
    {
        if (!await EnsureRarExecutableAvailableAsync(cancellationToken).ConfigureAwait(false))
        {
            return new ArchiveResult
            {
                Success = false,
                ExitCode = -2,
                ErrorMessage = GetMissingRarMessage()
            };
        }

        try
        {
            var arguments = WinRarCommandBuilder.BuildCompressionArguments(input, output, options);
            var result = await ExecuteRarCommand(arguments, cancellationToken, options.OutputLineHandler).ConfigureAwait(false);
            // 0.8.22：压缩返回码 1（有文件未写入）按失败处理，与 7-Zip 一致。
            return CompressionWarningPolicy.Apply(result, "RAR");
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            return new ArchiveResult
            {
                Success = false,
                ExitCode = -3,
                ErrorMessage = $"Exception during compression: {ex.Message}"
            };
        }
    }
    
    public async Task<ArchiveResult> ExtractAsync(string archivePath, string outputDir, ArchiveOptions options, CancellationToken cancellationToken = default)
    {
        if (!await EnsureRarExecutableAvailableAsync(cancellationToken).ConfigureAwait(false))
        {
            return new ArchiveResult
            {
                Success = false,
                ExitCode = -2,
                ErrorMessage = GetMissingRarMessage()
            };
        }

        try
        {
            var arguments = WinRarCommandBuilder.BuildExtractionArguments(archivePath, outputDir, options);
            return await ExecuteRarCommand(
                arguments, cancellationToken, options.OutputLineHandler,
                ArchiveSubProgressParser.TryParseFatalError).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        // WorkBuddy, 2026-11-15：批次 19（P4-1）——与 7zz 引擎保持一致的失败语义。
        catch (ArchiveFatalOutputException ex)
        {
            return new ArchiveResult
            {
                Success = false,
                ExitCode = -4,
                ErrorMessage = $"归档损坏，已提前终止：{ex.Error.Kind} — {ex.Error.Detail}（{ex.Error.SourceLine}）"
            };
        }
        catch (Exception ex)
        {
            return new ArchiveResult
            {
                Success = false,
                ExitCode = -3,
                ErrorMessage = $"Exception during extraction: {ex.Message}"
            };
        }
    }
    
    private async Task<ArchiveResult> ExecuteRarCommand(
        IReadOnlyList<string> arguments,
        CancellationToken cancellationToken,
        Action<string>? onOutputLine = null,
        // WorkBuddy, 2026-11-15：批次 19（P4-1）——仅解压路径传入，启用坏块提前终止。
        Func<string, ArchiveFatalError?>? onFatalLine = null)
    {
        var result = await new WinRarProcessRunner(_rarExecutablePath!)
            .RunAsync(arguments, cancellationToken, onOutputLine, onFatalLine)
            .ConfigureAwait(false);

        var success = WinRarExitCodes.IsSuccess(result.ExitCode);
        // GPT-5, 2026-08-05：进程标准输出和错误输出是原始诊断记录，必须保留其中的密码文本。
        // 禁止对 stdout/stderr 执行脱敏、替换或掩码；命令参数记录使用独立策略处理。
        var standardOutput = result.StandardOutput;
        var standardError = result.StandardError;
        return new ArchiveResult
        {
            Success = success,
            HasWarnings = success && result.ExitCode == WinRarExitCodes.Warning,
            ExitCode = result.ExitCode,
            StandardOutput = standardOutput,
            StandardError = standardError,
            CommandLine = result.CommandLine,
            ErrorMessage = success ? null : BuildFailureMessage(result.ExitCode, standardOutput, standardError)
        };
    }

    private string BuildFailureMessage(int exitCode, string standardOutput, string standardError)
    {
        var processMessage = string.IsNullOrWhiteSpace(standardError) ? standardOutput : standardError;
        return string.IsNullOrWhiteSpace(processMessage)
            ? GetRarErrorMessage(exitCode)
            : $"{GetRarErrorMessage(exitCode)}: {processMessage.Trim()}";
    }

    private string GetRarErrorMessage(int exitCode)
    {
        return exitCode switch
        {
            0 => "Success",
            1 => "Warning (non-fatal error)",
            2 => "Fatal error occurred",
            3 => "Data corruption detected",
            4 => "File is locked and cannot be modified",
            5 => "Cannot write to output",
            6 => "Cannot open file",
            7 => "Command line error",
            8 => "Not enough memory",
            9 => "Cannot create file",
            10 => "No files matching the specified mask found",
            11 => "Wrong password",
            255 => "User interrupted the operation",
            _ => "Unknown error"
        };
    }
}
