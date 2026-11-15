using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace BatchCompress.Avalonia.Core.Services;

/// <summary>
/// 崩溃兜底日志：全局未处理异常发生时，留下版本、运行时、线程与完整堆栈。
/// WorkBuddy, 2026-11-16：批次 21（P0）——与常规 FileLoggerService 分开，
/// 因为它必须在任何服务初始化之前就可用，而且自身绝不能再抛异常。
/// </summary>
public static class CrashLogService
{
    private const int MaxCrashFiles = 20;
    private const int SuppressWindowSeconds = 2;

    private static readonly object Gate = new();

    private static int _registered;
    // 同一毫秒内可能连写多条，文件名必须再加序号，否则会互相覆盖。
    private static int _sequence;
    private static DateTime _lastWriteUtc = DateTime.MinValue;
    private static string _lastSignature = string.Empty;
    private static int _suppressed;

    /// <summary>崩溃日志目录，默认与常规日志同级的 logs/。</summary>
    public static string LogDirectory =>
        DirectoryOverrideForTests ?? Path.Combine(AppContext.BaseDirectory, "logs");

    /// <summary>仅供测试指定隔离目录，生产路径保持 null。</summary>
    internal static string? DirectoryOverrideForTests { get; set; }

    /// <summary>
    /// 注册进程级与后台任务的未处理异常。重复调用安全（只注册一次）。
    /// 界面线程的未处理异常由 App 注册（Core 层不引用 Avalonia）。
    /// </summary>
    public static void RegisterGlobalHandlers()
    {
        if (Interlocked.Exchange(ref _registered, 1) == 1)
        {
            return;
        }

        AppDomain.CurrentDomain.UnhandledException += (_, e) =>
        {
            Write("appdomain", e.ExceptionObject as Exception
                ?? new Exception($"非异常终止对象：{e.ExceptionObject}"), "进程级未处理异常");
        };

        TaskScheduler.UnobservedTaskException += (_, e) =>
        {
            Write("task-scheduler", e.Exception, "后台任务未观察异常");
            // 记录后标记为已观察，避免未观察异常在回收时把进程带走。
            e.SetObserved();
        };
    }

    /// <summary>写一条崩溃记录。任何失败都只退回标准错误，绝不向外抛。</summary>
    public static void Write(string stage, Exception exception, string? context = null)
    {
        ArgumentNullException.ThrowIfNull(exception);

        try
        {
            var signature = BuildSignature(stage, exception);
            var suppressed = 0;
            lock (Gate)
            {
                var now = DateTime.UtcNow;
                if (signature == _lastSignature &&
                    now - _lastWriteUtc < TimeSpan.FromSeconds(SuppressWindowSeconds))
                {
                    _suppressed++;
                    return;
                }

                suppressed = _suppressed;
                _suppressed = 0;
                _lastSignature = signature;
                _lastWriteUtc = now;
            }

            // 异常对象自身的 ToString 也可能抛（自定义异常），单独兜一层。
            string detail;
            try
            {
                detail = exception.ToString();
            }
            catch (Exception toStringFailure)
            {
                detail = $"{exception.GetType().FullName}: {exception.Message}" +
                         $"\n（异常的 ToString() 也失败：{toStringFailure.GetType().Name}）";
            }

            WriteEntry(stage, detail, context, suppressed);
        }
        catch (Exception fallback)
        {
            TryConsole($"[CrashLog] {stage}: {exception.GetType().Name}: {exception.Message} " +
                       $"（崩溃日志写入失败：{fallback.Message}）");
        }
    }

    /// <summary>写一条非异常的致命诊断（例如启动期配置损坏）。</summary>
    public static void WriteMessage(string stage, string message, string? context = null)
    {
        try
        {
            var suppressed = 0;
            lock (Gate)
            {
                var now = DateTime.UtcNow;
                var signature = $"{stage}|message|{Trim(message, 120)}";
                if (signature == _lastSignature &&
                    now - _lastWriteUtc < TimeSpan.FromSeconds(SuppressWindowSeconds))
                {
                    _suppressed++;
                    return;
                }

                suppressed = _suppressed;
                _suppressed = 0;
                _lastSignature = signature;
                _lastWriteUtc = now;
            }

            WriteEntry(stage, message, context, suppressed);
        }
        catch (Exception fallback)
        {
            TryConsole($"[CrashLog] {stage}: {message}（崩溃日志写入失败：{fallback.Message}）");
        }
    }

    private static void WriteEntry(string stage, string detail, string? context, int suppressed)
    {
        var directory = LogDirectory;
        Directory.CreateDirectory(directory);

        var timestamp = DateTime.Now;
        var sequence = Interlocked.Increment(ref _sequence);
        var fileName =
            $"batchcompress-crash_{timestamp:yyyyMMdd_HHmmss_fff}_{Environment.ProcessId}_{sequence}.log";
        var path = Path.Combine(directory, fileName);

        var builder = new StringBuilder();
        builder.Append('[').Append(timestamp.ToString("yyyy-MM-dd HH:mm:ss.fff")).Append("] ");
        builder.Append("stage=").AppendLine(stage);
        builder.Append("version=").AppendLine(GetVersion());
        builder.Append("runtime=").AppendLine(RuntimeInformation.FrameworkDescription);
        builder.Append("os=").AppendLine(RuntimeInformation.OSDescription);
        builder.Append("arch=").AppendLine(RuntimeInformation.ProcessArchitecture.ToString());
        builder.Append("thread=").AppendLine(Environment.CurrentManagedThreadId.ToString());
        if (!string.IsNullOrWhiteSpace(context))
        {
            builder.Append("context=").AppendLine(context);
        }
        if (suppressed > 0)
        {
            builder.Append("suppressed=").AppendLine(suppressed.ToString());
        }
        builder.AppendLine("detail:");
        builder.AppendLine(detail);

        // 先写临时文件再移动，避免崩溃瞬间留下半截日志。
        var tempPath = path + ".tmp";
        File.WriteAllText(tempPath, builder.ToString(), new UTF8Encoding(false));
        File.Move(tempPath, path, overwrite: true);

        TryRestrictPermissions(path);
        // 写入之后再裁剪：先删到 20 再写会让总数变成 21。
        PruneOldFiles(directory);
    }

    /// <summary>仅供测试隔离静态去重状态。</summary>
    internal static void ResetStateForTests()
    {
        lock (Gate)
        {
            _lastWriteUtc = DateTime.MinValue;
            _lastSignature = string.Empty;
            _suppressed = 0;
        }
    }

    private static void PruneOldFiles(string directory)
    {
        try
        {
            var files = new DirectoryInfo(directory)
                .GetFiles("batchcompress-crash_*.log")
                .OrderByDescending(file => file.LastWriteTimeUtc)
                .Skip(MaxCrashFiles)
                .ToArray();

            foreach (var file in files)
            {
                try { file.Delete(); }
                catch { /* 清理失败不影响本次写入 */ }
            }
        }
        catch
        {
            // 目录枚举失败时跳过轮转，日志写入优先。
        }
    }

    private static void TryRestrictPermissions(string path)
    {
        if (OperatingSystem.IsWindows())
        {
            return;
        }

        try
        {
            File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite);
        }
        catch
        {
            // 某些文件系统不支持 Unix 权限位，忽略即可。
        }
    }

    private static string BuildSignature(string stage, Exception exception)
    {
        string message;
        try { message = exception.Message ?? string.Empty; }
        catch { message = string.Empty; }

        return $"{stage}|{exception.GetType().FullName}|{Trim(message, 120)}";
    }

    private static string Trim(string? value, int max)
    {
        if (string.IsNullOrEmpty(value)) return string.Empty;
        return value.Length <= max ? value : value[..max];
    }

    private static string GetVersion()
    {
        try
        {
            var assembly = Assembly.GetEntryAssembly() ?? typeof(CrashLogService).Assembly;
            return assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?
                .InformationalVersion?.Split('+')[0] ?? "unknown";
        }
        catch
        {
            return "unknown";
        }
    }

    private static void TryConsole(string message)
    {
        try { Console.Error.WriteLine(message); }
        catch { /* 控制台都不可用时无处可写，彻底放弃 */ }
    }
}
