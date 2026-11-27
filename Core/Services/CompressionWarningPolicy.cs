using BatchCompress.Avalonia.Core.Interfaces;

namespace BatchCompress.Avalonia.Core.Services;

/// <summary>
/// Claude Opus 5.5, 2026-11-21：0.8.22——压缩阶段的警告一律按失败处理。
/// 7-Zip/RAR 压缩时返回码 1 表示“有文件无法读取（无权限、被占用），未写入归档”，
/// 生成的归档不完整，且 7zz t 校验仍会通过。按用户决定：直接计为失败，
/// 由管线走既有失败回滚（删除本次不完整归档、恢复覆盖前的旧归档），并且不删除/移动来源。
/// 解压阶段的返回码 1 含义不同，不经过这里，仍由管线的 HasWarnings 拦截后处理。
/// </summary>
public static class CompressionWarningPolicy
{
    public static ArchiveResult Apply(ArchiveResult result, string toolName)
    {
        if (!result.HasWarnings)
        {
            return result;
        }

        // 7zz 把“WARNING: errno=13 : Permission denied”和文件路径写在标准错误；RAR 多写在标准输出。
        var detail = string.IsNullOrWhiteSpace(result.StandardError)
            ? result.StandardOutput.Trim()
            : result.StandardError.Trim();
        return result with
        {
            Success = false,
            ErrorMessage = string.IsNullOrWhiteSpace(detail)
                ? $"压缩不完整：{toolName} 返回警告（返回码 {result.ExitCode}），有文件未写入归档，已按失败处理"
                : $"压缩不完整：{toolName} 返回警告（返回码 {result.ExitCode}），有文件未写入归档，已按失败处理: {detail}"
        };
    }

    /// <summary>
    /// Gemini 4 Neon, 2026-11-26 / 2026-11-27：0.8.26——解压返回码 1 分级策略（方案 A）：
    /// - 写入失败、权限不足、路径/文件名受限或未知警告：默认也直接判失败并回滚本次残留；
    /// - 仅含跨平台属性或轻微元数据警告：默认放行（保留已解出文件，管线仍凭 HasWarnings 拦截删除/移动原包）；
    /// - 严格解压模式（strictExtract == true）：所有返回码 1（含可忽略警告）一律判失败。
    /// </summary>
    public static ArchiveResult ApplyForExtraction(ArchiveResult result, string toolName, bool strictExtract)
    {
        if (!result.HasWarnings)
        {
            return result;
        }

        var assessment = ExtractionWarningClassifier.Assess(result);
        if (!strictExtract && assessment.Severity == ExtractionWarningSeverity.Ignorable)
        {
            return result;
        }

        var detail = assessment.DiagnosticLines.Count > 0
            ? string.Join(" | ", assessment.DiagnosticLines)
            : (string.IsNullOrWhiteSpace(result.StandardError)
                ? result.StandardOutput.Trim()
                : result.StandardError.Trim());
        var modeNote = strictExtract && assessment.Severity == ExtractionWarningSeverity.Ignorable
            ? "严格模式"
            : assessment.Reason;

        return result with
        {
            Success = false,
            ErrorMessage = string.IsNullOrWhiteSpace(detail)
                ? $"解压不完整（{modeNote}）：{toolName} 返回警告（返回码 {result.ExitCode}），已按失败处理"
                : $"解压不完整（{modeNote}）：{toolName} 返回警告（返回码 {result.ExitCode}），已按失败处理: {detail}"
        };
    }
}
