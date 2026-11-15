namespace BatchCompress.Avalonia.ViewModels;

/// <summary>
/// 界面日志的有界缓冲与统一追加入口。
/// WorkBuddy, 2026-11-16：批次 22（P1）——所有日志写入都走这里，
/// 不再对字符串属性直接 `+=`，避免大批量任务下的平方级复制与无上限增长。
/// 只控制容量，不改变「命令日志含密码不脱敏」的既定内容策略。
/// </summary>
public partial class MainWindowViewModel
{
    private const int MaxLogLines = 5_000;
    private const int LogFlushIntervalMs = 200;

    private readonly LogBuffer _commandLogBuffer = new(MaxLogLines, LogFlushIntervalMs);
    private readonly LogBuffer _successLogBuffer = new(MaxLogLines, LogFlushIntervalMs);
    private readonly LogBuffer _failLogBuffer = new(MaxLogLines, LogFlushIntervalMs);

    internal void AppendCommandLog(string? text)
    {
        _commandLogBuffer.Append(text);
        if (_commandLogBuffer.TryFlush(out var snapshot)) CommandLog = snapshot;
    }

    internal void AppendSuccessLog(string? text)
    {
        _successLogBuffer.Append(text);
        if (_successLogBuffer.TryFlush(out var snapshot)) SuccessLog = snapshot;
    }

    internal void AppendFailLog(string? text)
    {
        _failLogBuffer.Append(text);
        if (_failLogBuffer.TryFlush(out var snapshot)) FailLog = snapshot;
    }

    /// <summary>批次结束、导出或关闭窗口前无条件刷新，避免最后几行因节流丢失。</summary>
    internal void ForceFlushLogs()
    {
        CommandLog = _commandLogBuffer.Flush();
        SuccessLog = _successLogBuffer.Flush();
        FailLog = _failLogBuffer.Flush();
    }

    internal void ResetCommandLog()
    {
        _commandLogBuffer.Clear();
        CommandLog = string.Empty;
    }

    internal void ResetSuccessLog()
    {
        _successLogBuffer.Clear();
        SuccessLog = string.Empty;
    }

    internal void ResetFailLog()
    {
        _failLogBuffer.Clear();
        FailLog = string.Empty;
    }
}
