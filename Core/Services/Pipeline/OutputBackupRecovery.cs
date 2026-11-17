using System;
using System.IO;

namespace BatchCompress.Avalonia.Core.Services.Pipeline;

/// <summary>
/// 覆盖写入的备份恢复：进程被强杀时可能留下 *.batchcompress-backup，
/// 下一次进入同一输出目录时先把它们恢复/清理，避免旧归档以备份名“消失”。
/// </summary>
internal static class OutputBackupRecovery
{
    internal const string BackupSuffix = ".batchcompress-backup";

    internal static void RecoverDirectory(string directory)
    {
        if (!Directory.Exists(directory))
        {
            return;
        }

        foreach (var backupPath in Directory.EnumerateFiles(
                     directory, "*" + BackupSuffix, SearchOption.TopDirectoryOnly))
        {
            var originalPath = backupPath[..^BackupSuffix.Length];
            TryRecoverPair(originalPath, backupPath);
        }
    }

    /// <summary>
    /// 最终文件缺失 → 恢复备份；最终文件存在 → 按时间判断：
    /// 备份更新说明上次替换失败，恢复备份；否则是成功后的陈旧备份，删除。
    /// </summary>
    internal static void TryRecoverPair(string originalPath, string backupPath)
    {
        try
        {
            if (!File.Exists(backupPath))
            {
                return;
            }

            if (!File.Exists(originalPath))
            {
                File.Move(backupPath, originalPath);
                return;
            }

            if (File.GetLastWriteTimeUtc(backupPath) > File.GetLastWriteTimeUtc(originalPath))
            {
                File.Delete(originalPath);
                File.Move(backupPath, originalPath);
                return;
            }

            File.Delete(backupPath);
        }
        catch
        {
            // 恢复是尽力而为；单个文件失败不阻止后续条目，下一次覆盖前仍会重试。
        }
    }
}
