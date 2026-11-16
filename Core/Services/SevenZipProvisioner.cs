using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;

namespace BatchCompress.Avalonia.Core.Services;

/// <summary>一次自动准备 7-Zip 的结果。</summary>
public sealed record SevenZipProvisionResult(
    bool Success,
    bool AlreadyAvailable,
    string? ExecutablePath,
    string Message);

// WorkBuddy, 2026-11-16：0.8.12——用户决定 Windows 产物不随包带 7-Zip，
// 缺少时从 7-Zip 官方渠道自动下载安装。
// 设计约束：
// 1) 只装在当前用户目录（LocalApplicationData\BatchCompress\7zip），不需要管理员权限；
// 2) 优先用官方 GitHub 发布接口，那里同时给出 SHA-256 摘要，下载后必须校验；
// 3) 接口不可用时退回官方下载页解析地址，此时只能依赖 HTTPS，日志里明确说明；
// 4) 先尝试 MSI 管理式解包（只展开文件、不写注册表），失败再退回 EXE 静默安装；
// 5) 安装完成后把 7z.exe 与 7z.dll 归位到固定目录，供归档引擎按固定路径查找。
public static class SevenZipProvisioner
{
    internal const string ReleaseApiUrl = "https://api.github.com/repos/ip7z/7zip/releases/latest";
    internal const string DownloadPageUrl = "https://www.7-zip.org/download.html";
    private const long MaxDownloadBytes = 64L * 1024 * 1024;

    /// <summary>当前用户下的固定安装目录；不写系统目录、不改系统环境变量。</summary>
    public static string InstallDirectory => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "BatchCompress",
        "7zip");

    /// <summary>归档引擎按这个固定路径查找自动安装的 7-Zip。</summary>
    public static string InstalledExecutablePath => Path.Combine(InstallDirectory, "7z.exe");

    /// <summary>在已知位置查找可用的 7z.exe；先看自动安装目录，再看系统安装位置。</summary>
    public static string? FindExistingExecutable()
    {
        foreach (var candidate in EnumerateExistingCandidates())
        {
            if (File.Exists(candidate))
            {
                return candidate;
            }
        }

        return null;
    }

    private static IEnumerable<string> EnumerateExistingCandidates()
    {
        // 自动安装目录必须同时有 7z.dll：只有 exe 时 WIM 等格式不可用，应视为未安装并重新准备。
        if (File.Exists(InstalledExecutablePath) &&
            File.Exists(Path.Combine(InstallDirectory, "7z.dll")))
        {
            yield return InstalledExecutablePath;
        }

        // 兼容旧版随包目录：即使当前发布不再携带，开发机上可能还留着。
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

    /// <summary>
    /// 确保 Windows 上有可用的 7-Zip；其他平台直接返回“无需处理”。
    /// 已存在任何一份 7z.exe 时不会下载。
    /// </summary>
    public static async Task<SevenZipProvisionResult> EnsureAsync(
        IProgress<string>? progress = null,
        CancellationToken cancellationToken = default)
    {
        if (!OperatingSystem.IsWindows())
        {
            return new SevenZipProvisionResult(true, true, null, "Only Windows requires 7-Zip provisioning.");
        }

        var existing = FindExistingExecutable();
        if (existing != null)
        {
            return new SevenZipProvisionResult(true, true, existing, $"7-Zip already available: {existing}");
        }

        try
        {
            var assets = await ResolveAssetsAsync(cancellationToken).ConfigureAwait(false);
            if (assets.Count == 0)
            {
                return Failure("No official 7-Zip download could be resolved.");
            }

            foreach (var asset in assets)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var result = await TryInstallAsync(asset, progress, cancellationToken).ConfigureAwait(false);
                if (result.Success)
                {
                    return result;
                }

                progress?.Report(result.Message);
            }

            return Failure(
                "Automatic 7-Zip installation failed. Install it manually from https://www.7-zip.org/download.html and retry.");
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[7-Zip] 自动安装失败: {ex}");
            return Failure($"Automatic 7-Zip installation failed: {ex.Message}");
        }
    }

    private static SevenZipProvisionResult Failure(string message) =>
        new(false, false, null, message);

    private static async Task<SevenZipProvisionResult> TryInstallAsync(
        SevenZipAsset asset,
        IProgress<string>? progress,
        CancellationToken cancellationToken)
    {
        var extension = Path.GetExtension(asset.Name);
        var temporaryFile = Path.Combine(
            Path.GetTempPath(),
            $"batchcompress-7zip-{Guid.NewGuid():N}{extension}");

        try
        {
            progress?.Report($"Downloading official 7-Zip package: {asset.Name}");
            await DownloadAsync(asset.Url, temporaryFile, progress, cancellationToken).ConfigureAwait(false);

            if (!string.IsNullOrWhiteSpace(asset.Sha256))
            {
                var actual = await ComputeSha256Async(temporaryFile, cancellationToken).ConfigureAwait(false);
                if (!string.Equals(actual, asset.Sha256, StringComparison.OrdinalIgnoreCase))
                {
                    return Failure($"SHA-256 mismatch for {asset.Name}; download rejected.");
                }
            }
            else
            {
                progress?.Report("Official checksum unavailable; relying on HTTPS for this download.");
            }

            // 上一次失败可能留下半成品，安装前清空自有目录，避免误把旧文件当成新安装。
            TryClearInstallDirectory();

            var extracted = await ExtractAsync(temporaryFile, cancellationToken).ConfigureAwait(false);
            if (!extracted)
            {
                return Failure($"Could not extract {asset.Name}.");
            }

            var installed = NormalizeInstallLayout();
            if (installed == null)
            {
                return Failure($"{asset.Name} did not contain 7z.exe and 7z.dll.");
            }

            if (!await ValidateAsync(installed, cancellationToken).ConfigureAwait(false))
            {
                return Failure("The installed 7z.exe failed its identity check.");
            }

            progress?.Report($"7-Zip installed: {installed}");
            return new SevenZipProvisionResult(true, false, installed, $"7-Zip installed: {installed}");
        }
        finally
        {
            TryDeleteFile(temporaryFile);
        }
    }

    private static async Task<bool> ExtractAsync(string packagePath, CancellationToken cancellationToken)
    {
        if (packagePath.EndsWith(".msi", StringComparison.OrdinalIgnoreCase))
        {
            // 管理式安装只把文件展开到目标目录，不注册组件、不写系统位置，普通用户即可执行。
            var exitCode = await RunProcessAsync(
                "msiexec",
                ["/a", packagePath, "/qn", $"TARGETDIR={InstallDirectory}"],
                cancellationToken).ConfigureAwait(false);
            if (exitCode == 0)
            {
                return true;
            }

            Debug.WriteLine($"[7-Zip] MSI 解包退出码 {exitCode}，改用 EXE 安装包。");
            return false;
        }

        if (packagePath.EndsWith(".exe", StringComparison.OrdinalIgnoreCase))
        {
            // NSIS 静默安装：/S 不显示界面，/D= 指定当前用户目录（必须是最后一个参数）。
            var exitCode = await RunProcessAsync(
                packagePath,
                ["/S", $"/D={InstallDirectory}"],
                cancellationToken).ConfigureAwait(false);
            return exitCode == 0;
        }

        return false;
    }

    /// <summary>把 7z.exe / 7z.dll 归位到安装根目录，返回最终可执行文件路径。</summary>
    internal static string? NormalizeInstallLayout(string? installDirectory = null)
    {
        var root = installDirectory ?? InstallDirectory;
        if (!Directory.Exists(root))
        {
            return null;
        }

        var executable = Directory
            .EnumerateFiles(root, "7z.exe", SearchOption.AllDirectories)
            .OrderBy(path => path.Count(c => c == Path.DirectorySeparatorChar))
            .FirstOrDefault();
        if (executable == null)
        {
            return null;
        }

        var sourceDirectory = Path.GetDirectoryName(executable)!;
        if (!string.Equals(
                Path.GetFullPath(sourceDirectory),
                Path.GetFullPath(root),
                StringComparison.OrdinalIgnoreCase))
        {
            foreach (var name in new[] { "7z.exe", "7z.dll", "7zFM.exe", "License.txt", "readme.txt" })
            {
                var source = Path.Combine(sourceDirectory, name);
                if (File.Exists(source))
                {
                    File.Copy(source, Path.Combine(root, name), overwrite: true);
                }
            }
        }

        var finalExecutable = Path.Combine(root, "7z.exe");
        var library = Path.Combine(root, "7z.dll");
        // WIM 等格式依赖 7z.dll；缺它时不能算安装成功，否则会在实际任务里才暴露。
        return File.Exists(finalExecutable) && File.Exists(library) ? finalExecutable : null;
    }

    private static async Task<bool> ValidateAsync(string executablePath, CancellationToken cancellationToken)
    {
        try
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(TimeSpan.FromSeconds(10));
            var result = await new ArchiveProcessRunner(executablePath, "7-Zip")
                .RunAsync(["i"], timeout.Token)
                .ConfigureAwait(false);
            var output = result.StandardOutput + result.StandardError;
            return SevenZipExitCodes.IsSuccess(result.ExitCode) &&
                   output.Contains("7-Zip", StringComparison.OrdinalIgnoreCase);
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[7-Zip] 安装后身份校验失败: {ex.Message}");
            return false;
        }
    }

    private static async Task<List<SevenZipAsset>> ResolveAssetsAsync(CancellationToken cancellationToken)
    {
        // 首选官方 GitHub 发布接口：同时拿到下载地址与 SHA-256 摘要。
        try
        {
            using var client = CreateClient();
            var json = await client.GetStringAsync(ReleaseApiUrl, cancellationToken).ConfigureAwait(false);
            if (TryParseReleaseJson(json, out var assets))
            {
                return assets;
            }
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[7-Zip] 官方发布接口不可用: {ex.Message}");
        }

        // 退回官方下载页；解析不到校验值，安装流程会在日志里注明。
        try
        {
            using var client = CreateClient();
            var html = await client.GetStringAsync(DownloadPageUrl, cancellationToken).ConfigureAwait(false);
            if (TryParseDownloadPage(html, out var asset) && asset != null)
            {
                return [asset];
            }
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[7-Zip] 官方下载页不可用: {ex.Message}");
        }

        return [];
    }

    /// <summary>从 GitHub 发布接口的 JSON 解析 64 位安装包；优先 MSI，EXE 作为后备。</summary>
    internal static bool TryParseReleaseJson(string json, out List<SevenZipAsset> assets)
    {
        assets = [];
        using var document = JsonDocument.Parse(json);
        if (!document.RootElement.TryGetProperty("assets", out var array) ||
            array.ValueKind != JsonValueKind.Array)
        {
            return false;
        }

        SevenZipAsset? msi = null;
        SevenZipAsset? exe = null;
        foreach (var element in array.EnumerateArray())
        {
            var name = element.TryGetProperty("name", out var nameElement)
                ? nameElement.GetString()
                : null;
            if (string.IsNullOrWhiteSpace(name) ||
                !Regex.IsMatch(name, @"^7z\d+-x64\.(msi|exe)$", RegexOptions.IgnoreCase))
            {
                continue;
            }

            var url = element.TryGetProperty("browser_download_url", out var urlElement)
                ? urlElement.GetString()
                : null;
            if (string.IsNullOrWhiteSpace(url))
            {
                continue;
            }

            string? sha256 = null;
            if (element.TryGetProperty("digest", out var digestElement) &&
                digestElement.ValueKind == JsonValueKind.String)
            {
                var digest = digestElement.GetString();
                if (digest != null && digest.StartsWith("sha256:", StringComparison.OrdinalIgnoreCase))
                {
                    sha256 = digest["sha256:".Length..];
                }
            }

            var size = element.TryGetProperty("size", out var sizeElement) &&
                       sizeElement.TryGetInt64(out var parsedSize)
                ? parsedSize
                : 0;

            var asset = new SevenZipAsset(name, url, sha256, size);
            if (name.EndsWith(".msi", StringComparison.OrdinalIgnoreCase))
            {
                msi ??= asset;
            }
            else
            {
                exe ??= asset;
            }
        }

        if (msi != null)
        {
            assets.Add(msi);
        }

        if (exe != null)
        {
            assets.Add(exe);
        }

        return assets.Count > 0;
    }

    /// <summary>从官方下载页解析 64 位 MSI 链接，作为接口不可用时的兜底。</summary>
    internal static bool TryParseDownloadPage(string html, out SevenZipAsset? asset)
    {
        asset = null;
        var patterns = new[]
        {
            @"https://github\.com/ip7z/7zip/releases/download/[^""'\s]+/7z\d+-x64\.msi",
            @"https://www\.7-zip\.org/a/7z\d+-x64\.msi"
        };

        foreach (var pattern in patterns)
        {
            var match = Regex.Match(html, pattern, RegexOptions.IgnoreCase);
            if (!match.Success)
            {
                continue;
            }

            var url = match.Value;
            asset = new SevenZipAsset(Path.GetFileName(url), url, null, 0);
            return true;
        }

        return false;
    }

    private static HttpClient CreateClient()
    {
        var client = new HttpClient { Timeout = TimeSpan.FromMinutes(3) };
        // GitHub 接口要求带 User-Agent，否则会返回 403；版本号与程序集信息同源。
        client.DefaultRequestHeaders.UserAgent.ParseAdd(
            $"BatchCompress/{ApplicationInfoService.ReadApplicationVersion()}");
        return client;
    }

    private static async Task DownloadAsync(
        string url,
        string targetPath,
        IProgress<string>? progress,
        CancellationToken cancellationToken)
    {
        using var client = CreateClient();
        using var response = await client
            .GetAsync(url, HttpCompletionOption.ResponseHeadersRead, cancellationToken)
            .ConfigureAwait(false);
        response.EnsureSuccessStatusCode();

        var contentLength = response.Content.Headers.ContentLength;
        if (contentLength is > MaxDownloadBytes)
        {
            throw new InvalidOperationException($"Download is too large: {contentLength} bytes.");
        }

        await using var source = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
        await using var target = new FileStream(
            targetPath, FileMode.Create, FileAccess.Write, FileShare.None, 81920, useAsync: true);

        var buffer = new byte[81920];
        long total = 0;
        var lastReported = 0;
        int read;
        while ((read = await source.ReadAsync(buffer, cancellationToken).ConfigureAwait(false)) > 0)
        {
            total += read;
            if (total > MaxDownloadBytes)
            {
                throw new InvalidOperationException("Download exceeded the allowed size.");
            }

            await target.WriteAsync(buffer.AsMemory(0, read), cancellationToken).ConfigureAwait(false);

            if (contentLength is > 0)
            {
                var percent = (int)(total * 100 / contentLength.Value);
                if (percent >= lastReported + 25)
                {
                    lastReported = percent;
                    progress?.Report($"Downloading 7-Zip: {percent}%");
                }
            }
        }
    }

    internal static async Task<string> ComputeSha256Async(string path, CancellationToken cancellationToken)
    {
        await using var stream = File.OpenRead(path);
        var hash = await SHA256.HashDataAsync(stream, cancellationToken).ConfigureAwait(false);
        return Convert.ToHexString(hash).ToLowerInvariant();
    }

    private static async Task<int> RunProcessAsync(
        string fileName,
        IReadOnlyList<string> arguments,
        CancellationToken cancellationToken)
    {
        var info = new ProcessStartInfo
        {
            FileName = fileName,
            UseShellExecute = false,
            CreateNoWindow = true
        };
        foreach (var argument in arguments)
        {
            info.ArgumentList.Add(argument);
        }

        using var process = Process.Start(info) ?? throw new InvalidOperationException($"Cannot start {fileName}");
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromMinutes(5));
        try
        {
            await process.WaitForExitAsync(timeout.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            try
            {
                if (!process.HasExited)
                {
                    process.Kill(entireProcessTree: true);
                }
            }
            catch
            {
                // 进程已退出或无法结束，保留原始异常语义。
            }

            throw;
        }

        return process.ExitCode;
    }

    private static void TryClearInstallDirectory()
    {
        try
        {
            if (!Directory.Exists(InstallDirectory))
            {
                Directory.CreateDirectory(InstallDirectory);
                return;
            }

            foreach (var file in Directory.EnumerateFiles(InstallDirectory, "*", SearchOption.AllDirectories))
            {
                TryDeleteFile(file);
            }

            foreach (var directory in Directory
                         .EnumerateDirectories(InstallDirectory, "*", SearchOption.AllDirectories)
                         .OrderByDescending(path => path.Length))
            {
                try
                {
                    Directory.Delete(directory);
                }
                catch
                {
                    // 目录非空或正被占用时不影响后续解包。
                }
            }
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[7-Zip] 清理安装目录失败: {ex.Message}");
        }
    }

    private static void TryDeleteFile(string path)
    {
        try
        {
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
        catch
        {
            // 临时文件清理失败不应改变安装结果。
        }
    }
}

/// <summary>官方发布中的一个 64 位安装包。</summary>
public sealed record SevenZipAsset(string Name, string Url, string? Sha256, long Size);
