using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using BatchCompress.Avalonia.Core.Interfaces;

namespace BatchCompress.Avalonia.Core.Services;

// GPT-5, 2026-08-06：7z 创建与解压统一调用 7-Zip 官方独立命令行程序 7zz。
// macOS/Linux 优先使用项目随包分发的二进制，系统安装路径仅作为开发与兼容后备。
public sealed class SevenZipArchiveEngine : IArchiveEngine
{
    private readonly string? _configuredExecutablePath;
    private string? _executablePath;

    public SevenZipArchiveEngine(string? executablePath = null)
    {
        _configuredExecutablePath = executablePath;
    }

    public bool IsAvailable() =>
        IsAvailableAsync(CancellationToken.None).GetAwaiter().GetResult();

    public async Task<bool> IsAvailableAsync(CancellationToken cancellationToken = default)
    {
        _executablePath = await FindExecutableAsync(cancellationToken).ConfigureAwait(false);
        return !string.IsNullOrEmpty(_executablePath);
    }

    public async Task<ArchiveResult> CompressAsync(
        string input,
        string output,
        ArchiveOptions options,
        CancellationToken cancellationToken = default)
    {
        if (!await EnsureExecutableAvailableAsync(cancellationToken).ConfigureAwait(false))
        {
            return MissingExecutableResult();
        }

        try
        {
            var arguments = SevenZipCommandBuilder.BuildCompressionArguments(input, output, options);
            var result = await ExecuteAsync(arguments, cancellationToken, options.OutputLineHandler).ConfigureAwait(false);
            // 0.8.22：创建阶段返回码 1（有文件未写入）直接判失败，不再进入校验阶段。
            result = CompressionWarningPolicy.Apply(result, "7-Zip");
            if (!result.Success || !options.TestArchive)
            {
                return result;
            }

            // GPT-5, 2026-08-06：7zz 没有 WinRAR 的“创建后自动测试”开关，因此创建成功后显式执行 t。
            // 7zz 分卷不会保留基础输出文件，必须使用实际首卷作为 t 命令入口。
            var testPath = ResolveTestPath(output);
            var testArguments = SevenZipCommandBuilder.BuildTestArguments(testPath, options.Password);
            var testResult = await ExecuteAsync(testArguments, cancellationToken, options.OutputLineHandler).ConfigureAwait(false);
            // GPT-5, 2026-08-06：创建和校验是两次进程，返回时合并两者原始输出，避免丢失第一阶段诊断信息。
            var mergedTestResult = testResult with
            {
                CommandLine = string.Join(
                    Environment.NewLine,
                    new[] { result.CommandLine, testResult.CommandLine }
                        .Where(line => !string.IsNullOrWhiteSpace(line))),
                StandardOutput = JoinOutput(result.StandardOutput, testResult.StandardOutput),
                StandardError = JoinOutput(result.StandardError, testResult.StandardError)
            };
            // 创建阶段的警告已在上面判失败；校验阶段若也返回警告，同样按压缩不完整处理。
            return CompressionWarningPolicy.Apply(mergedTestResult, "7-Zip");
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
                ErrorMessage = $"7z 压缩异常: {ex.Message}"
            };
        }
    }

    public async Task<ArchiveResult> ExtractAsync(
        string archivePath,
        string outputDir,
        ArchiveOptions options,
        CancellationToken cancellationToken = default)
    {
        if (!await EnsureExecutableAvailableAsync(cancellationToken).ConfigureAwait(false))
        {
            return MissingExecutableResult();
        }

        try
        {
            var arguments = SevenZipCommandBuilder.BuildExtractionArguments(archivePath, outputDir, options);
            var result = await ExecuteAsync(
                arguments, cancellationToken, options.OutputLineHandler,
                ArchiveSubProgressParser.TryParseFatalError).ConfigureAwait(false);
            return CompressionWarningPolicy.ApplyForExtraction(result, "7-Zip", options.StrictExtract);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        // WorkBuddy, 2026-11-15：批次 19（P4-1）——坏块提前终止转成明确的失败结果，
        // 由执行管线走既有的失败回滚与计数，不会被误当成用户取消。
        catch (ArchiveFatalOutputException ex)
        {
            // ErrorMessage 会由上层服务写入日志文件并展示给用户，携带 Kind / 短语 / 原始行便于精确定位。
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
                ErrorMessage = $"7z 解压异常: {ex.Message}"
            };
        }
    }

    private async Task<bool> EnsureExecutableAvailableAsync(CancellationToken cancellationToken)
    {
        return !string.IsNullOrEmpty(_executablePath) ||
               await IsAvailableAsync(cancellationToken).ConfigureAwait(false);
    }

    private async Task<string?> FindExecutableAsync(CancellationToken cancellationToken)
    {
        foreach (var candidate in GetCandidatePaths())
        {
            cancellationToken.ThrowIfCancellationRequested();
            var (isValid, message) = await TryValidateExecutableAsync(candidate, cancellationToken).ConfigureAwait(false);
            if (isValid)
            {
                Debug.WriteLine($"[7-Zip] 已选路径: {candidate}");
                return candidate;
            }

            Debug.WriteLine($"[7-Zip] 拒绝: {candidate}（{message}）");
        }

        return null;
    }

    private IEnumerable<string> GetCandidatePaths()
    {
        if (!string.IsNullOrWhiteSpace(_configuredExecutablePath))
        {
            yield return Path.GetFullPath(_configuredExecutablePath);
            yield break;
        }

        // WorkBuddy, 2026-11-17：0.8.16——生产路径不再信任“当前工作目录”。
        // 命令行常在下载来的项目/临时目录里运行，目录中的伪造 tools/7zip/.../7zz
        // 会被当成可信程序执行；需要自定义路径时用显式环境变量。
        var explicitPath = Environment.GetEnvironmentVariable("BATCHCOMPRESS_7ZZ_PATH");
        if (!string.IsNullOrWhiteSpace(explicitPath))
        {
            yield return Path.GetFullPath(explicitPath);
            yield break;
        }

        if (OperatingSystem.IsMacOS())
        {
            yield return Path.Combine(AppContext.BaseDirectory, "tools", "7zip", "macos", "7zz");
            yield return "/opt/homebrew/bin/7zz";
            yield return "/usr/local/bin/7zz";
        }
        else if (OperatingSystem.IsLinux())
        {
            var platformDirectory = RuntimeInformation.ProcessArchitecture == Architecture.Arm64
                ? "linux-arm64"
                : "linux-x64";
            yield return Path.Combine(AppContext.BaseDirectory, "tools", "7zip", platformDirectory, "7zz");
            yield return "/usr/local/bin/7zz";
            yield return "/usr/bin/7zz";
        }
        else if (OperatingSystem.IsWindows())
        {
            // WorkBuddy, 2026-11-16：0.8.12——用户决定 Windows 产物不随包带 7-Zip。
            // 首选自动安装目录（当前用户、无需管理员），再找系统安装位置。
            yield return SevenZipProvisioner.InstalledExecutablePath;

            // 兼容旧版随包目录：发布已不再携带，但开发机上可能残留。
            yield return Path.Combine(AppContext.BaseDirectory, "tools", "7zip", "windows-x64", "7z.exe");

            var programFiles = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles);
            if (!string.IsNullOrWhiteSpace(programFiles))
            {
                yield return Path.Combine(programFiles, "7-Zip", "7z.exe");
            }

            var programFilesX86 = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86);
            if (!string.IsNullOrWhiteSpace(programFilesX86))
            {
                yield return Path.Combine(programFilesX86, "7-Zip", "7z.exe");
            }

            var localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
            if (!string.IsNullOrWhiteSpace(localAppData))
            {
                yield return Path.Combine(localAppData, "Programs", "7-Zip", "7z.exe");
            }
        }
    }

    private static async Task<(bool IsValid, string Message)> TryValidateExecutableAsync(
        string path,
        CancellationToken cancellationToken)
    {
        try
        {
            if (!File.Exists(path))
            {
                return (false, "文件不存在");
            }

            if (!OperatingSystem.IsWindows())
            {
                var mode = File.GetUnixFileMode(path);
                if ((mode & (UnixFileMode.UserExecute | UnixFileMode.GroupExecute | UnixFileMode.OtherExecute)) == 0)
                {
                    File.SetUnixFileMode(path, mode | UnixFileMode.UserExecute | UnixFileMode.GroupExecute | UnixFileMode.OtherExecute);
                }
            }

            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(TimeSpan.FromSeconds(3));
            var result = await new ArchiveProcessRunner(path, "7-Zip")
                .RunAsync(["i"], timeout.Token)
                .ConfigureAwait(false);
            var output = result.StandardOutput + result.StandardError;
            if (!SevenZipExitCodes.IsSuccess(result.ExitCode) ||
                !output.Contains("7-Zip", StringComparison.OrdinalIgnoreCase))
            {
                return (false, $"身份校验失败，返回码 {result.ExitCode}");
            }

            return (true, string.Empty);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            return (false, ex.Message);
        }
    }

    private async Task<ArchiveResult> ExecuteAsync(
        IReadOnlyList<string> arguments,
        CancellationToken cancellationToken,
        Action<string>? onOutputLine = null,
        // WorkBuddy, 2026-11-15：批次 19（P4-1）——仅解压路径传入，启用坏块提前终止。
        Func<string, ArchiveFatalError?>? onFatalLine = null)
    {
        var result = await new ArchiveProcessRunner(_executablePath!, "7-Zip")
            .RunAsync(arguments, cancellationToken, onOutputLine, onFatalLine)
            .ConfigureAwait(false);
        var success = SevenZipExitCodes.IsSuccess(result.ExitCode);

        // GPT-5, 2026-08-06：stdout/stderr 是用户要求保留的原始进程输出，其中的密码明确不脱敏、不替换为 ***。
        return new ArchiveResult
        {
            Success = success,
            // 返回码 1 = 非致命警告（例如文件无权限未被压入），由管线拦截后处理。
            HasWarnings = success && result.ExitCode == SevenZipExitCodes.Warning,
            ExitCode = result.ExitCode,
            StandardOutput = result.StandardOutput,
            StandardError = result.StandardError,
            CommandLine = result.CommandLine,
            ErrorMessage = success ? null : BuildFailureMessage(result)
        };
    }

    private static string BuildFailureMessage(ArchiveProcessResult result)
    {
        var detail = string.IsNullOrWhiteSpace(result.StandardError)
            ? result.StandardOutput.Trim()
            : result.StandardError.Trim();
        var summary = result.ExitCode switch
        {
            2 => "7-Zip 严重错误",
            7 => "7-Zip 命令行参数错误",
            8 => "7-Zip 内存不足",
            255 => "7-Zip 操作被用户中止",
            _ => "7-Zip 未知错误"
        };
        return string.IsNullOrWhiteSpace(detail) ? summary : $"{summary}: {detail}";
    }

    private static ArchiveResult MissingExecutableResult() => new()
    {
        Success = false,
        ExitCode = -2,
        // WorkBuddy, 2026-11-16：0.8.12——Windows 会自动下载安装，给出失败后的可操作指令。
        ErrorMessage = OperatingSystem.IsWindows()
            ? "未找到 7-Zip 命令行程序 7z，自动下载安装也未成功。请联网后重试，或从 https://www.7-zip.org/download.html 手动安装。"
            : "未找到 7-Zip 官方命令行程序 7zz。"
    };

    private static string JoinOutput(string first, string second)
    {
        if (string.IsNullOrEmpty(first))
        {
            return second;
        }

        if (string.IsNullOrEmpty(second))
        {
            return first;
        }

        return first.TrimEnd() + Environment.NewLine + second;
    }

    private static string ResolveTestPath(string output)
    {
        try
        {
            var resolved = ArchiveVolumeResolver.Resolve(output);
            return resolved.FirstVolumePath ?? output;
        }
        catch
        {
            // 创建结果已经成功时，解析失败不应隐藏原始输出；7zz 会给出最终校验诊断。
            return output;
        }
    }
}
