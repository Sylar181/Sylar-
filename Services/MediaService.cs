using System.Globalization;
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
    public async Task DownloadAsync(string remoteMediaPath, string localPath, CancellationToken cancellationToken)
    {
        var temp = $"/data/local/tmp/hfm_media_{Guid.NewGuid():N}.tmp";
        try
        {
            await _hdc.RunShellAsync(_deviceId, $"mediatool recv {Quote(remoteMediaPath)} {Quote(temp)}", cancellationToken);
            await _hdc.ReceiveFileAsync(_deviceId, temp, localPath, cancellationToken);
            await _audit.WriteAsync("media-download", $"{remoteMediaPath} -> {localPath}");
        }
        finally
        {
            try { await _hdc.RunShellAsync(_deviceId, $"rm -f {Quote(temp)}", CancellationToken.None, 5); } catch { /* 清理失败忽略 */ }
        }
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
