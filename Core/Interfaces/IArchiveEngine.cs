using System;
using System.Threading;
using System.Threading.Tasks;

namespace BatchCompress.Avalonia.Core.Interfaces;

/// <summary>
/// 压缩和解压引擎接口。
/// </summary>
// GPT-5, 2026-08-05：归档引擎接收已解析路径和取消令牌；实现不能依赖特定界面或 Shell，
// 必须通过 ArchiveResult 暴露工具相关失败。
public interface IArchiveEngine
{
    /// <summary>
    /// 压缩文件或目录。
    /// </summary>
    Task<ArchiveResult> CompressAsync(string input, string output, ArchiveOptions options, CancellationToken cancellationToken = default);
    
    /// <summary>
    /// 解压归档。
    /// </summary>
    Task<ArchiveResult> ExtractAsync(string archivePath, string outputDir, ArchiveOptions options, CancellationToken cancellationToken = default);
    
    /// <summary>
    /// 检查当前系统是否存在可用归档引擎。
    /// </summary>
    bool IsAvailable();

    /// <summary>
    /// 异步检查当前系统是否存在可用归档引擎，避免在异步或界面上下文中同步阻塞子进程校验。
    /// </summary>
    Task<bool> IsAvailableAsync(CancellationToken cancellationToken = default) =>
        Task.FromResult(IsAvailable());
    
}

/// <summary>
/// 压缩和解压操作选项。
/// </summary>
public class ArchiveOptions
{
    public string ArchiveFormat { get; set; } = "rar";
    public string? Password { get; set; }
    public string? TempDirectory { get; set; }
    public string? CommentFile { get; set; }
    public CompressionLevel CompressionLevel { get; set; } = CompressionLevel.Normal;
    public bool SolidArchive { get; set; }
    public bool QuickOpen { get; set; }
    public bool TestArchive { get; set; }
    public string? VolumeSize { get; set; }
    public ExistingFileMode ExistingFileMode { get; set; } = ExistingFileMode.Skip;
    public int RecoveryRecordPercent { get; set; }
    // GPT-5, 2026-08-07：仅供 WinRarCommandBuilder 生成按扩展名仅存储的 -ms 参数；不是排除清单。
    public string[]? RarStoreOnlyExtensions { get; set; }
    public bool LockArchive { get; set; }
    public string[]? AdditionalInputs { get; set; }
    // Antigravity, 2026-11-08：批次 17（P3-11）——归档引擎原始输出行流式回调，用于解析实时分卷与任务子进度。
    public Action<string>? OutputLineHandler { get; set; }
    // Gemini 4 Neon, 2026-11-26：批次 36——严格解压模式，开启后解压返回码 1（警告）也按失败处理。
    public bool StrictExtract { get; set; }
}

/// <summary>
/// 归档操作结果（不可变记录，避免跨阶段复用或并发读写时被原地改写）。
/// </summary>
public sealed record ArchiveResult
{
    public bool Success { get; init; }
    // Claude Opus 5.5, 2026-11-20：0.8.20——归档程序返回码 1（非致命警告）。7-Zip/RAR 在部分文件
    // 无法读取时也返回 1，归档可能缺文件；为 true 时管线禁止删除/移动来源，避免数据丢失。
    public bool HasWarnings { get; init; }
    public int ExitCode { get; init; }
    public string? ErrorMessage { get; init; }
    public string StandardOutput { get; init; } = string.Empty;
    public string StandardError { get; init; } = string.Empty;
    public string CommandLine { get; init; } = string.Empty;
}

/// <summary>
/// 压缩级别。
/// </summary>
public enum CompressionLevel
{
    Store = 0,      // No compression
    Fastest = 1,    // Light compression
    Fast = 2,       // Fast
    Normal = 3,     // Normal
    Good = 4,       // Good
    Best = 5        // Maximum
}

/// <summary>
/// 已有文件处理模式。
/// </summary>
public enum ExistingFileMode
{
    Skip,           // Skip existing files
    Update,         // Update existing files
    Overwrite       // Overwrite existing files
}
