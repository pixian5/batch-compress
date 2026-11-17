using System;
using System.Collections.Generic;
using System.Linq;
using System.IO;
using System.Threading.Tasks;
using BatchCompress.Avalonia.Core.Models;
using BatchCompress.Avalonia.Core.Services;

// WorkBuddy, 2026-11-17：0.8.16——安全边界回归：
// 1) 7-Zip 查找不得信任当前工作目录；2) 报告/NDJSON 与诊断包共用同一脱敏实现。
internal static partial class Program
{
    private static Task TestCredentialRedactorSharedRules()
    {
        const string quotedSecret = "Super Secret 123";
        const string assignmentSecret = "Equals Secret 789";

        var quoted = CredentialRedactor.Redact($"app compress \"-p{quotedSecret}\" -e 7z");
        Assert(!quoted.Contains(quotedSecret, StringComparison.Ordinal), "引号整体 -p 密码仍残留");
        Assert(quoted.Contains("\"-p***\"", StringComparison.Ordinal), "引号整体 -p 未替换");

        var assignment = CredentialRedactor.Redact($"app compress \"--password={assignmentSecret}\"");
        Assert(!assignment.Contains(assignmentSecret, StringComparison.Ordinal), "引号整体 --password= 密码仍残留");

        // 普通路径里的 -pictures / -hp-data 不能被误伤。
        const string ordinaryPath = "/tmp/-pictures/photo.jpg";
        AssertEqual(ordinaryPath, CredentialRedactor.Redact(ordinaryPath));
        Assert(!CredentialRedactor.ContainsCredential(ordinaryPath), "普通路径不应被判定为凭据");
        Assert(CredentialRedactor.ContainsCredential($"\"-p{quotedSecret}\""), "引号整体凭据未被识别");
        return Task.CompletedTask;
    }

    private static Task TestReportAndNdjsonRedactQuotedCredentials()
    {
        const string secret = "Super Secret 123";
        var message = $"\"-p{secret}\"";

        var collector = new BatchReportCollector();
        collector.Report(new OperationProgressInfo { SuccessCount = 1, Message = message });
        AssertEqual(1, collector.Entries.Count);
        AssertEqual(BatchReportCollector.RedactedMarker, collector.Entries[0].Message);

        using var writer = new StringWriter();
        var ndjson = new NdjsonOutputWriter(writer);
        ndjson.Report(new OperationProgressInfo { SuccessCount = 1, CurrentFile = "a.7z", Message = message });
        var output = writer.ToString();
        Assert(!output.Contains(secret, StringComparison.Ordinal), "NDJSON 仍带出引号整体密码");
        Assert(output.Contains("-p***", StringComparison.Ordinal), "NDJSON 未替换引号整体密码");
        return Task.CompletedTask;
    }


    private static Task TestCliTextFileDiagnosticsMerge()
    {
        var import = new TextFileImportResult();
        import.Entries.Add(new FileEntry { FilePath = "/valid/a.7z" });
        import.MissingEntries.Add("/missing/b.7z");
        import.IncompleteVolumes.Add("/incomplete/c.7z");
        import.AmbiguousEntries.Add("/ambiguous/d.7z");
        import.DuplicateVolumeEntries.Add("/duplicate/e.7z");

        var entries = new List<FileEntry>(import.Entries);
        BatchCompress.Avalonia.HeadlessBatchRunner.AppendTextFileDiagnostics(entries, import);

        AssertEqual(5, entries.Count);
        Assert(entries.Any(entry => entry.FilePath == "/missing/b.7z"), "TXT 缺失项必须进入任务列表");
        Assert(entries.Any(entry => entry.FilePath == "/incomplete/c.7z"), "缺卷项必须进入任务列表");
        return Task.CompletedTask;
    }

    private static Task TestOutputBackupRecovery()
    {
        var root = Path.Combine(Path.GetTempPath(), $"backup-recovery-{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);
        var original = Path.Combine(root, "a.7z");
        var backup = original + ".batchcompress-backup";
        File.WriteAllText(backup, "old archive");

        try
        {
            BatchCompress.Avalonia.Core.Services.Pipeline.OutputBackupRecovery.RecoverDirectory(root);
            Assert(File.Exists(original), "最终文件缺失时必须恢复备份");
            AssertEqual("old archive", File.ReadAllText(original));
            Assert(!File.Exists(backup), "恢复后不应残留备份");
        }
        finally
        {
            if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
        }

        return Task.CompletedTask;
    }

    private static Task TestSevenZipDoesNotTrustCurrentDirectory()
    {
        var source = File.ReadAllText(
            Path.Combine(FindRepositoryRoot(), "Core", "Services", "SevenZipArchiveEngine.cs"));

        Assert(!source.Contains("Directory.GetCurrentDirectory()", StringComparison.Ordinal),
            "7-Zip 查找不得包含当前工作目录");
        Assert(source.Contains("BATCHCOMPRESS_7ZZ_PATH", StringComparison.Ordinal),
            "7-Zip 应支持显式环境变量指定路径");
        return Task.CompletedTask;
    }
}
