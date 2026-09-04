using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Net.Http;

namespace HarmonyFileManager.Services;

/// <summary>
/// 自动获取 hdc.exe 并安装到工具目录 toolchains 下:
/// - 从可配置的压缩包直链下载解压(DownloadAndInstallAsync);
/// - 或从本地压缩包安装(InstallFromArchiveAsync);
/// 安装完成后运行 hdc 校验可执行性,通过才返回路径。
/// 自动下载仅作为引导便利,不绕过系统授权与设备授权边界。
/// </summary>
public static class HdcToolchainInstaller
{
    /// <summary>自动安装目录(随工具分发或自动下载都放这里)。</summary>
    public static string ToolchainsDir { get; } = Path.Combine(AppContext.BaseDirectory, "toolchains");

    public static async Task<string?> DownloadAndInstallAsync(string url, IProgress<string>? progress, CancellationToken cancellationToken)
    {
        var tempZip = Path.Combine(Path.GetTempPath(), $"hfm_hdc_{Guid.NewGuid():N}.zip");
        try
        {
            progress?.Report($"开始下载:{url}");
            using var client = new HttpClient { Timeout = TimeSpan.FromMinutes(30) };
            using var response = await client.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
            response.EnsureSuccessStatusCode();
            var total = response.Content.Headers.ContentLength;
            await using var source = await response.Content.ReadAsStreamAsync(cancellationToken);
            await using var file = File.Create(tempZip);
            var buffer = new byte[81920];
            long downloaded = 0;
            int read;
            while ((read = await source.ReadAsync(buffer, cancellationToken)) > 0)
            {
                await file.WriteAsync(buffer.AsMemory(0, read), cancellationToken);
                downloaded += read;
                progress?.Report(total is > 0
                    ? $"下载中 {FormatSize(downloaded)} / {FormatSize(total.Value)}"
                    : $"下载中 {FormatSize(downloaded)}");
            }
            progress?.Report("下载完成,正在解压安装…");
            return InstallFromZipCore(tempZip, progress, cancellationToken);
        }
        finally
        {
            try { File.Delete(tempZip); } catch { /* 忽略 */ }
        }
    }

    /// <summary>从本地 zip 压缩包安装(内含 hdc.exe,通常在 toolchains 目录)。</summary>
    public static Task<string?> InstallFromArchiveAsync(string archivePath, IProgress<string>? progress, CancellationToken cancellationToken)
        => Task.Run(() => InstallFromZipCore(archivePath, progress, cancellationToken), cancellationToken);

    private static string? InstallFromZipCore(string zipPath, IProgress<string>? progress, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var extractDir = Path.Combine(Path.GetTempPath(), $"hfm_hdc_extract_{Guid.NewGuid():N}");
        Directory.CreateDirectory(extractDir);
        try
        {
            progress?.Report("正在解压压缩包…");
            ExtractSafely(zipPath, extractDir);
            var sourceExe = FindHdcExecutable(extractDir);
            if (sourceExe == null)
            {
                progress?.Report("压缩包中未找到 hdc.exe。");
                return null;
            }

            var destDir = ToolchainsDir;
            Directory.CreateDirectory(destDir);
            progress?.Report("正在复制 hdc.exe 到工具目录…");
            // 同目录可能还有配套文件(如 DLL),一并复制,避免只拷 exe 导致运行失败
            CopyDirectoryFiles(Path.GetDirectoryName(sourceExe)!, destDir);
            var destExe = Path.Combine(destDir, "hdc.exe");
            if (!File.Exists(destExe)) throw new InvalidOperationException("安装失败:未能写入 hdc.exe。");

            progress?.Report("正在校验 hdc 可执行性…");
            if (!VerifyAsync(destExe, CancellationToken.None).GetAwaiter().GetResult())
            {
                throw new InvalidOperationException("安装的 hdc.exe 校验失败,无法以 hdc 方式运行,请检查压缩包来源。");
            }
            return destExe;
        }
        finally
        {
            try { Directory.Delete(extractDir, true); } catch { /* 忽略 */ }
        }
    }

    /// <summary>运行 hdc 校验其可执行性(version 子命令,10 秒超时)。</summary>
    public static async Task<bool> VerifyAsync(string hdcPath, CancellationToken cancellationToken)
    {
        foreach (var argument in new[] { "version", "-v" })
        {
            try
            {
                var info = new ProcessStartInfo(hdcPath)
                {
                    UseShellExecute = false,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    CreateNoWindow = true
                };
                info.ArgumentList.Add(argument);
                using var process = Process.Start(info);
                if (process == null) continue;
                using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                timeout.CancelAfter(TimeSpan.FromSeconds(10));
                await process.WaitForExitAsync(timeout.Token);
                if (process.ExitCode == 0) return true;
            }
            catch
            {
                // 尝试下一种参数形式
            }
        }
        return false;
    }

    /// <summary>优先 toolchains\hdc.exe;否则递归查找(限深度),返回找到的第一个 hdc.exe。</summary>
    private static string? FindHdcExecutable(string root)
    {
        var direct = Path.Combine(root, "toolchains", "hdc.exe");
        if (File.Exists(direct)) return direct;
        return FindHdcCore(root, 0);
    }

    private static string? FindHdcCore(string dir, int depth)
    {
        if (depth > 6) return null;
        try
        {
            foreach (var file in Directory.EnumerateFiles(dir, "hdc.exe"))
            {
                return file;
            }
            foreach (var sub in Directory.EnumerateDirectories(dir))
            {
                if (Path.GetFileName(sub).Equals("toolchains", StringComparison.OrdinalIgnoreCase))
                {
                    var toolchainExe = Path.Combine(sub, "hdc.exe");
                    if (File.Exists(toolchainExe)) return toolchainExe;
                }
                if (FindHdcCore(sub, depth + 1) is { } hit) return hit;
            }
        }
        catch
        {
            // 无权限或占位目录,跳过
        }
        return null;
    }

    /// <summary>安全解压:拒绝 ../ 越界与绝对路径条目。</summary>
    private static void ExtractSafely(string zipPath, string destDir)
    {
        var destFull = Path.GetFullPath(destDir);
        using var archive = ZipFile.OpenRead(zipPath);
        foreach (var entry in archive.Entries)
        {
            var normalized = entry.FullName.Replace('/', Path.DirectorySeparatorChar);
            var target = Path.GetFullPath(Path.Combine(destDir, normalized));
            if (!target.StartsWith(destFull, StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidOperationException("压缩包包含越界路径,已中止安装。");
            }
            if (string.IsNullOrEmpty(entry.Name))
            {
                Directory.CreateDirectory(target);
                continue;
            }
            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            entry.ExtractToFile(target, overwrite: true);
        }
    }

    private static void CopyDirectoryFiles(string sourceDir, string destDir)
    {
        foreach (var file in Directory.EnumerateFiles(sourceDir))
        {
            File.Copy(file, Path.Combine(destDir, Path.GetFileName(file)), overwrite: true);
        }
    }

    private static string FormatSize(long bytes)
        => bytes >= 1024 * 1024 ? $"{bytes / 1024.0 / 1024.0:0.0} MB" : $"{bytes / 1024.0:0} KB";
}
