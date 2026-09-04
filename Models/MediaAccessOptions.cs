using System.IO;

namespace HarmonyFileManager.Models;

/// <summary>系统媒体库(相册/视频)访问配置。经 mediatool 特权通道只读访问。</summary>
public sealed class MediaAccessOptions
{
    /// <summary>是否启用相册下载功能。</summary>
    public bool Enabled { get; init; } = true;

    /// <summary>媒体库根路径,HarmonyOS NEXT 标准为 /storage/media/100/local/files。</summary>
    public required string MediaRoot { get; init; }

    /// <summary>
    /// 下载保存目录。支持:
    ///   - 绝对路径,如 G:\hm\downloads
    ///   - 占位符,如 %Desktop%\HarmonyDownloads、%Documents%\HarmonyDownloads、%UserProfile%\Downloads
    ///   - 相对路径(相对于 exe 所在目录),如 downloads
    /// 为空或解析失败时自动降级到 桌面\HarmonyDownloads。
    /// </summary>
    public string DownloadFolder { get; init; } = string.Empty;

    /// <summary>支持的媒体类型:键为界面展示名,值为 mediatool 路径下的目录名(Photo/Video)。</summary>
    public required Dictionary<string, string> MediaTypes { get; init; }

    /// <summary>
    /// 解析下载目录:替换占位符 → 检查可写性 → 不可用时降级到桌面。
    /// 返回最终可用的绝对路径,并确保目录已创建。
    /// </summary>
    public string ResolveDownloadFolder()
    {
        var candidates = new List<string>();

        if (!string.IsNullOrWhiteSpace(DownloadFolder))
        {
            candidates.Add(ExpandPlaceholders(DownloadFolder));
        }
        // 兜底候选:桌面 → 文档 → 用户目录 → 临时目录
        candidates.Add(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Desktop), "HarmonyDownloads"));
        candidates.Add(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments), "HarmonyDownloads"));
        candidates.Add(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "HarmonyDownloads"));
        candidates.Add(Path.Combine(Path.GetTempPath(), "HarmonyDownloads"));

        foreach (var candidate in candidates)
        {
            try
            {
                var full = Path.IsPathRooted(candidate)
                    ? candidate
                    : Path.Combine(AppContext.BaseDirectory, candidate);
                Directory.CreateDirectory(full);
                // 可写性探测:创建一个临时文件并删除
                var probe = Path.Combine(full, $".write_probe_{Guid.NewGuid():N}");
                File.WriteAllText(probe, string.Empty);
                File.Delete(probe);
                return full;
            }
            catch { /* 继续试下一个候选 */ }
        }

        // 极端情况:所有候选都不可用,返回临时目录(调用方需处理创建失败)
        return Path.Combine(Path.GetTempPath(), "HarmonyDownloads");
    }

    private static string ExpandPlaceholders(string path)
    {
        return path
            .Replace("%Desktop%", Environment.GetFolderPath(Environment.SpecialFolder.Desktop), StringComparison.OrdinalIgnoreCase)
            .Replace("%Documents%", Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments), StringComparison.OrdinalIgnoreCase)
            .Replace("%UserProfile%", Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), StringComparison.OrdinalIgnoreCase)
            .Replace("%Temp%", Path.GetTempPath().TrimEnd(Path.DirectorySeparatorChar), StringComparison.OrdinalIgnoreCase)
            .Replace("%AppData%", Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), StringComparison.OrdinalIgnoreCase);
    }
}
