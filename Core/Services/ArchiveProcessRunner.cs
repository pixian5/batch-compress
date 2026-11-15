using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace BatchCompress.Avalonia.Core.Services;

public sealed record ArchiveProcessResult(
    int ExitCode,
    string StandardOutput,
    string StandardError,
    string CommandLine);

// GPT-5, 2026-08-06：所有归档程序共用这一进程边界。参数逐项写入 ArgumentList，
// stdout/stderr 同时异步读取以防止管道阻塞，取消时终止整个子进程树。
public sealed class ArchiveProcessRunner
{
    private readonly string _executablePath;
    private readonly string _toolName;

    public ArchiveProcessRunner(string executablePath, string toolName)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(executablePath);
        ArgumentException.ThrowIfNullOrWhiteSpace(toolName);
        _executablePath = executablePath;
        _toolName = toolName;
    }

    public async Task<ArchiveProcessResult> RunAsync(
        IReadOnlyList<string> arguments,
        CancellationToken cancellationToken,
        Action<string>? onOutputLine = null,
        Func<string, ArchiveFatalError?>? onFatalLine = null)
    {
        cancellationToken.ThrowIfCancellationRequested();

        var commandLine = BuildCommandLine(_executablePath, arguments);

        using var process = new Process
        {
            StartInfo = new ProcessStartInfo
            {
                FileName = _executablePath,
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true
            }
        };

        foreach (var argument in arguments)
        {
            process.StartInfo.ArgumentList.Add(argument);
        }

        if (!process.Start())
        {
            throw new InvalidOperationException($"无法启动 {_toolName}: {_executablePath}");
        }

        // WorkBuddy, 2026-11-15：批次 19（P4-1）——致命输出错误走独立的联动令牌。
        // 只有传入 onFatalLine 的解压路径才启用；压缩路径与旧行为逐字节一致。
        using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        var fatalState = new FatalState();

        void CheckFatal(string line)
        {
            if (onFatalLine is null || linkedCts.IsCancellationRequested) return;

            ArchiveFatalError? fatal;
            try
            {
                fatal = onFatalLine(line);
            }
            catch
            {
                // 解析器异常绝不能影响归档进程本身，降级为「不提前终止」。
                return;
            }

            if (fatal is null) return;
            lock (fatalState.Gate)
            {
                fatalState.Error ??= fatal;
            }

            try { linkedCts.Cancel(); }
            catch (ObjectDisposedException) { }
        }

        Action<string>? stdoutHandler = null;
        if (onOutputLine is not null || onFatalLine is not null)
        {
            stdoutHandler = line =>
            {
                try { onOutputLine?.Invoke(line); }
                finally { CheckFatal(line); }
            };
        }

        // 实测 7-Zip 的 "ERROR: CRC Failed" 写的是 stderr，必须同样逐行流式读取，
        // 否则等进程结束才拿到整段输出，就做不到「提前终止、减少无效磁盘读取」。
        var standardOutputTask = ReadStreamWithCallbackAsync(process.StandardOutput, stdoutHandler);
        var standardErrorTask = onFatalLine is null
            ? process.StandardError.ReadToEndAsync()
            : ReadStreamWithCallbackAsync(process.StandardError, line => CheckFatal(line));

        try
        {
            await process.WaitForExitAsync(linkedCts.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (linkedCts.IsCancellationRequested)
        {
            TryKill(process);
            await WaitForExitAfterKillAsync(process).ConfigureAwait(false);
            await Task.WhenAll(standardOutputTask, standardErrorTask).ConfigureAwait(false);
            ThrowFatalIfNeeded(fatalState, cancellationToken);
            throw;
        }

        await Task.WhenAll(standardOutputTask, standardErrorTask).ConfigureAwait(false);
        // 进程恰好与取消同时自然退出时，仍要把致命错误转换成失败结果，避免竞态漏判。
        ThrowFatalIfNeeded(fatalState, cancellationToken);
        return new ArchiveProcessResult(
            process.ExitCode,
            await standardOutputTask.ConfigureAwait(false),
            await standardErrorTask.ConfigureAwait(false),
            commandLine);
    }

    private static void ThrowFatalIfNeeded(FatalState state, CancellationToken externalToken)
    {
        ArchiveFatalError? fatal;
        lock (state.Gate)
        {
            fatal = state.Error;
        }

        // 外部用户取消优先：它必须继续按取消语义向上传播，不能被误判成归档损坏。
        if (fatal is not null && !externalToken.IsCancellationRequested)
        {
            throw new ArchiveFatalOutputException(fatal);
        }
    }

    private sealed class FatalState
    {
        public readonly object Gate = new();
        public ArchiveFatalError? Error;
    }

    private static async Task<string> ReadStreamWithCallbackAsync(
        StreamReader reader,
        Action<string>? onOutputLine)
    {
        if (onOutputLine == null)
        {
            return await reader.ReadToEndAsync().ConfigureAwait(false);
        }

        var fullOutput = new StringBuilder();
        var lineBuffer = new StringBuilder();
        var buffer = new char[1024];
        int read;

        while ((read = await reader.ReadAsync(buffer, 0, buffer.Length).ConfigureAwait(false)) > 0)
        {
            fullOutput.Append(buffer, 0, read);
            for (var i = 0; i < read; i++)
            {
                var ch = buffer[i];
                if (ch is '\r' or '\n')
                {
                    if (lineBuffer.Length > 0)
                    {
                        var line = lineBuffer.ToString();
                        lineBuffer.Clear();
                        try
                        {
                            onOutputLine(line);
                        }
                        catch
                        {
                            // 进度流回调异常不阻断进程输出流的完整读取
                        }
                    }
                }
                else
                {
                    lineBuffer.Append(ch);
                }
            }
        }

        if (lineBuffer.Length > 0)
        {
            var line = lineBuffer.ToString();
            try
            {
                onOutputLine(line);
            }
            catch
            {
                // 忽略回调异常
            }
        }

        return fullOutput.ToString();
    }

    // 保留可直接复制核对的完整参数；调用方明确要求日志不脱敏，因此密码也原样保留。
    private static string BuildCommandLine(string executablePath, IReadOnlyList<string> arguments)
    {
        return string.Join(" ", new[] { executablePath }
            .Concat(arguments)
            .Select(QuoteCommandArgument));
    }

    private static string QuoteCommandArgument(string value)
    {
        if (value.Length == 0)
        {
            return "\"\"";
        }

        if (value.All(character => !char.IsWhiteSpace(character) && character != '\"'))
        {
            return value;
        }

        return $"\"{value.Replace("\\", "\\\\").Replace("\"", "\\\"")}\"";
    }

    private static async Task WaitForExitAfterKillAsync(Process process)
    {
        try
        {
            await process.WaitForExitAsync().ConfigureAwait(false);
        }
        catch (InvalidOperationException)
        {
            // GPT-5, 2026-08-06：进程可能在取消检查和终止之间自行退出，此时无需再次处理。
        }
    }

    private static void TryKill(Process process)
    {
        try
        {
            if (!process.HasExited)
            {
                process.Kill(entireProcessTree: true);
            }
        }
        catch (InvalidOperationException)
        {
            // GPT-5, 2026-08-06：进程已在状态检查后退出，视为取消清理完成。
        }
    }
}
