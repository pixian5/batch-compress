namespace BatchCompress.Avalonia.Core.Models;

// GPT-5, 2026-08-07：按实际压缩特征整理默认清单。RAR 使用这些扩展名生成 -ms，仅存储而不排除。
// 7z/ZIP 不读取本清单；它们由 SevenZipCommandBuilder 使用自己的参数模型处理。
public static class ArchiveDefaults
{
    // WorkBuddy, 2026-11-16：0.8.12——用户决定统一图形界面与命令行的默认压缩参数。
    // 这里是唯一来源：命令行选项初值、界面字段初值与界面侧的兜底取值都读它，
    // 避免两个入口各写一套默认值导致同一任务产出不同结果。
    // 压缩级别取值 0-5，3 对应“标准”。
    public const int DefaultCompressionLevel = 3;

    /// <summary>恢复记录默认百分比；0 表示关闭。</summary>
    public const int DefaultRecoveryRecordPercent = 3;

    /// <summary>是否默认启用分卷；统一为关闭，避免用户意外得到多个分卷文件。</summary>
    public const bool DefaultEnableVolume = false;

    /// <summary>分卷大小的默认数值（仅在启用分卷时生效）。</summary>
    public const string DefaultVolumeSize = "20";

    /// <summary>命令行分卷单位默认值；界面下拉索引 0 表示 GB，与此处一致。</summary>
    public const string DefaultVolumeUnit = "g";

    /// <summary>
    /// 通常已经压缩或编码，重复压缩收益很低，RAR 默认仅存储这些文件。
    /// </summary>
    public static readonly string[] StoreOnlyExtensions =
    [
        // 已有归档或压缩流。
        "7z", "ace", "arj", "bz2", "cab", "gz", "lha", "lz", "lzh", "rar", "taz", "tgz",
        "xz", "z", "zip", "zipx",
        // 有损或无损编码媒体。
        "aac", "avi", "flac", "flv", "m4a", "mkv", "mov", "mp3", "mp4", "ogg", "opus", "rm", "rmvb", "webm",
        // 常见压缩图片和图片容器。
        "avif", "gif", "heic", "jpeg", "jpg", "png", "webp",
        // 本身是 ZIP 容器的办公文档。
        "docx", "odg", "odp", "ods", "odt", "pptx", "xlsx"
    ];

    /// <summary>
    /// 通常仍有较多重复数据，适合使用正常压缩算法处理。
    /// 该清单用于文档和测试分类，不会传给 7z/ZIP，也不会自动排除其他扩展名。
    /// </summary>
    public static readonly string[] CompressionFriendlyExtensions =
    [
        "bmp", "csv", "cs", "cpp", "css", "c", "conf", "config", "dat", "doc", "h", "html", "ini",
        "java", "js", "json", "log", "md", "ppt", "py", "rs", "rtf", "sh", "sql", "svg", "tar", "ts",
        "tsv", "txt", "wav", "xls", "xml", "yaml", "yml"
    ];

    /// <summary>
    /// 是否值得再次压缩取决于内部编码或所包含文件，默认不强制归类。
    /// </summary>
    public static readonly string[] ContentDependentExtensions =
    [
        "dmg", "iso", "pdf", "tif", "tiff"
    ];
}
