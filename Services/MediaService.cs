using System.Globalization;
using System.IO;
using System.Text.RegularExpressions;
using HarmonyFileManager.Models;

namespace HarmonyFileManager.Services;

/// <summary>
/// 通过 HarmonyOS 内置 mediatool 访问系统媒体库(相册/视频),只读下载。
/// 列表用 mediatool ls -l,下载用 mediatool recv 到设备临时目录再 hdc file recv 回本机。
/// 媒体库为系统级数据,shell 与普通应用身份均无法直接访问(/storage/media Permission denied),
/// 必须经 mediatool 特权通道(它以媒体服务身份读取 MediaLibrary)。
/// </summary>
public sealed class MediaService
{
    // mediatool ls -l 输出为 ISO 日期格式(2026-06-16 16:09),需单独正则。
    private static readonly Regex MediaListRegex = new(
        @"^(?<perm>[d\-lbcsp])(?<mode>[rwxstST\-]{9})\s+\d+\s+\S+\s+\S+\s+(?<size>\d+)\s+(?<date>\d{4}-\d{2}-\d{2})\s+(?<time>\d{1,2}:\d{2})\s+(?<name>.+)$",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    private readonly HdcService _hdc;
    private readonly AuditService _audit;
    private readonly string _deviceId;
    private readonly string _mediaRoot;

    public MediaService(HdcService hdc, AuditService audit, string deviceId, string mediaRoot)
    {
        _hdc = hdc;
        _audit = audit;
        _deviceId = deviceId;
        _mediaRoot = mediaRoot.TrimEnd('/');
    }

    public string DeviceId => _deviceId;

    /// <summary>列出指定媒体类型(Photo/Video)下的所有分桶目录。</summary>
    public Task<IReadOnlyList<MediaItem>> ListBucketsAsync(string mediaType, CancellationToken cancellationToken)
        => ListMediaDirectoryAsync($"{_mediaRoot}/{mediaType}", cancellationToken);

    /// <summary>列出指定分桶内的媒体文件。</summary>
    public Task<IReadOnlyList<MediaItem>> ListFilesAsync(string mediaType, string bucketId, CancellationToken cancellationToken)
        => ListMediaDirectoryAsync($"{_mediaRoot}/{mediaType}/{bucketId}", cancellationToken);

    private async Task<IReadOnlyList<MediaItem>> ListMediaDirectoryAsync(string mediaPath, CancellationToken cancellationToken)
    {
        var output = await _hdc.RunShellAsync(_deviceId, $"mediatool ls -l {Quote(mediaPath)}", cancellationToken);
        return ParseListing(output, mediaPath);
    }

    /// <summary>下载单个媒体文件:mediatool recv 到设备临时目录 → hdc file recv 回本机。
    /// 大文件由 hdc file recv 的 300s 超时覆盖。</summary>
    public async Task DownloadAsync(string remoteMediaPath, string localPath, CancellationToken cancellationToken,
        IProgress<TransferProgress>? progress = null, long? expectedSize = null)
    {
        var temp = $"/data/local/tmp/hfm_media_{Guid.NewGuid():N}.tmp";
        var staging = Path.Combine(Path.GetDirectoryName(Path.GetFullPath(localPath))!, $".hfm_{Guid.NewGuid():N}.partial");
        var id = Guid.NewGuid().ToString("N");
        var total = expectedSize is >= 0 ? checked(expectedSize.Value * 2) : -1;
        void Report(long bytes, string stage, bool measured = true, string state = "running") => progress?.Report(new TransferProgress
        {
            TransferId = id, Direction = "download", FileName = remoteMediaPath[(remoteMediaPath.LastIndexOf('/') + 1)..],
            BytesTransferred = total >= 0 ? Math.Clamp(bytes, 0, total) : Math.Max(0, bytes),
            TotalBytes = measured ? total : -1, Stage = stage, State = state
        });
        async Task MonitorAsync(Task transfer, Func<Task<long>> size, long offset, string stage)
        {
            Report(offset, stage, false);
            while (!transfer.IsCompleted)
            {
                var bytes = -1L;
                try { bytes = await size(); }
                catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { }
                catch { /* 监测失败不能影响传输任务本身。 */ }
                Report(offset + Math.Max(0, bytes), stage, bytes >= 0);
                await Task.WhenAny(transfer, Task.Delay(250));
            }
            await transfer;
        }
        try
        {
            var receive = _hdc.RunShellAsync(_deviceId, $"mediatool recv {Quote(remoteMediaPath)} {Quote(temp)}", cancellationToken, 300);
            await MonitorAsync(receive, () => GetTemporarySizeAsync(temp, cancellationToken), 0, "读取媒体到设备临时目录");
            var stagedSize = await GetTemporarySizeAsync(temp, cancellationToken);
            if (stagedSize < 0 || (expectedSize is >= 0 && stagedSize != expectedSize))
                throw new IOException("媒体临时文件大小无法确认或不完整。");
            total = checked(stagedSize * 2);
            var download = _hdc.ReceiveFileAsync(_deviceId, temp, staging, cancellationToken);
            await MonitorAsync(download, () => Task.FromResult(File.Exists(staging) ? new FileInfo(staging).Length : 0L), stagedSize, "下载到本机");
            if (new FileInfo(staging).Length != stagedSize) throw new IOException("媒体下载不完整。");
            File.Move(staging, localPath, overwrite: true);
            await _audit.WriteAsync("media-download", $"{remoteMediaPath} -> {localPath}");
            Report(total, "完成", true, "done");
        }
        catch (Exception exception)
        {
            Report(0, "失败", false, "failed");
            await _audit.WriteAsync("media-download-failed", $"{remoteMediaPath}: {exception.Message}");
            throw;
        }
        finally
        {
            try { if (File.Exists(staging)) File.Delete(staging); } catch { /* 尽力清理。 */ }
            try { await _hdc.RunShellAsync(_deviceId, $"rm -f {Quote(temp)}", CancellationToken.None, 5); } catch { /* 清理失败忽略 */ }
        }
    }

    private async Task<long> GetTemporarySizeAsync(string path, CancellationToken cancellationToken)
    {
        var output = await _hdc.RunShellAsync(_deviceId,
            $"stat -c %s {Quote(path)} 2>/dev/null || wc -c < {Quote(path)}", cancellationToken, 5);
        var line = output.Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries).LastOrDefault()?.Trim();
        if (line?.StartsWith("--", StringComparison.Ordinal) == true) line = line[2..].Trim();
        return long.TryParse(line, NumberStyles.Integer, CultureInfo.InvariantCulture, out var size) && size >= 0 ? size : -1;
    }

    private static IReadOnlyList<MediaItem> ParseListing(string output, string remoteDir)
    {
        var items = new List<MediaItem>();
        foreach (var rawLine in output.Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries))
        {
            var line = rawLine.Trim();
            if (line.StartsWith("--", StringComparison.Ordinal)) line = line[2..].TrimStart();
            if (line.Length == 0 || line.StartsWith("total ", StringComparison.Ordinal)) continue;
            if (line.StartsWith("[FAIL]", StringComparison.Ordinal)) continue;

            var match = MediaListRegex.Match(line);
            if (!match.Success) continue;
            var name = match.Groups["name"].Value;
            if (name is "." or "..") continue;

            var isDirectory = match.Groups["perm"].Value == "d";
            var size = long.Parse(match.Groups["size"].Value, CultureInfo.InvariantCulture);
            var dateStr = $"{match.Groups["date"].Value} {match.Groups["time"].Value}";
            DateTimeOffset? modified = DateTime.TryParseExact(dateStr, "yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture, DateTimeStyles.None, out var parsed)
                ? new DateTimeOffset(parsed) : null;

            items.Add(new MediaItem
            {
                Name = name,
                Type = isDirectory ? "directory" : "file",
                Path = $"{remoteDir.TrimEnd('/')}/{name}",
                Size = isDirectory ? null : size,
                ModifiedAt = modified
            });
        }
        return items;
    }

    private static string Quote(string path) => $"\"{path.Replace("\"", "\\\"")}\"";
}
