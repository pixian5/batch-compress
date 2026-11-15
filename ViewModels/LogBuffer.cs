using System;
using System.Collections.Generic;
using System.Text;

namespace BatchCompress.Avalonia.ViewModels;

/// <summary>
/// 界面日志的有界缓冲。
/// WorkBuddy, 2026-11-16：批次 22（P1）——原来每次 `+=` 都复制整段字符串并全量刷新，
/// 大批量任务下是 O(n²) 且内存只增不减；这里改为按行入队、超限丢最旧、
/// 并按时间节流把快照推给绑定属性。
/// </summary>
public sealed class LogBuffer
{
    private readonly object _gate = new();
    private readonly Queue<string> _lines = new();
    private readonly int _flushIntervalMs;

    private int _omitted;
    private long _lastFlushTicks;
    private bool _dirty;

    public LogBuffer(int maxLines = 5_000, int flushIntervalMs = 200)
    {
        MaxLines = maxLines > 0 ? maxLines : 5_000;
        _flushIntervalMs = flushIntervalMs > 0 ? flushIntervalMs : 200;
    }

    public int MaxLines { get; }

    /// <summary>因超出上限被丢弃的最早行数。</summary>
    public int OmittedCount
    {
        get
        {
            lock (_gate) return _omitted;
        }
    }

    /// <summary>是否有尚未推送给界面的新内容。</summary>
    public bool IsDirty
    {
        get
        {
            lock (_gate) return _dirty;
        }
    }

    /// <summary>
    /// 追加一段文本（可含换行）。按行拆分后入队；超出上限时丢弃最早的行并累计省略数。
    /// </summary>
    public void Append(string? text)
    {
        if (string.IsNullOrEmpty(text)) return;

        // 兼容 \r\n、\n、\r 三种换行，避免 Windows 日志在界面里变成双空行。
        var normalized = text.Replace("\r\n", "\n").Replace('\r', '\n');
        var segments = normalized.Split('\n');

        lock (_gate)
        {
            for (var i = 0; i < segments.Length; i++)
            {
                var segment = segments[i];
                // 只跳过「末尾换行产生的最后一个空段」，必须用索引判断：
                // 字符串拆分产生的所有空段都是同一个 string.Empty 引用，
                // 用引用相等会把中间的分隔空行一并误删。
                if (segment.Length == 0 && i == segments.Length - 1)
                {
                    continue;
                }

                _lines.Enqueue(segment);
                if (_lines.Count > MaxLines)
                {
                    _lines.Dequeue();
                    _omitted++;
                }
            }

            _dirty = true;
        }
    }

    /// <summary>
    /// 到达刷新间隔时返回 true 并给出完整文本；未到间隔或没有新内容返回 false。
    /// 用时间戳而不是定时器，避免引入额外的生命周期管理。
    /// </summary>
    public bool TryFlush(out string text)
    {
        lock (_gate)
        {
            text = string.Empty;
            if (!_dirty) return false;

            var now = Environment.TickCount64;
            if (_lastFlushTicks != 0 && now - _lastFlushTicks < _flushIntervalMs)
            {
                return false;
            }

            text = BuildTextLocked();
            _dirty = false;
            _lastFlushTicks = now;
            return true;
        }
    }

    /// <summary>无条件刷新（批次结束、清空、导出前调用）。</summary>
    public string Flush()
    {
        lock (_gate)
        {
            var text = BuildTextLocked();
            _dirty = false;
            _lastFlushTicks = Environment.TickCount64;
            return text;
        }
    }

    public void Clear()
    {
        lock (_gate)
        {
            _lines.Clear();
            _omitted = 0;
            _dirty = false;
            _lastFlushTicks = 0;
        }
    }

    private string BuildTextLocked()
    {
        var builder = new StringBuilder();
        if (_omitted > 0)
        {
            builder.Append($"（已省略最早的 {_omitted} 行）").Append('\n');
        }

        foreach (var line in _lines)
        {
            builder.Append(line).Append('\n');
        }

        return builder.ToString();
    }
}
