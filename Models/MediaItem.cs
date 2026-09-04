namespace HarmonyFileManager.Models;

/// <summary>媒体库中的一项:分桶(directory)或媒体文件(file)。</summary>
public sealed class MediaItem
{
    public required string Name { get; init; }
    public required string Type { get; init; }
    public string Path { get; init; } = string.Empty;
    public long? Size { get; init; }
    public DateTimeOffset? ModifiedAt { get; init; }

    public string SizeDisplay => Type == "directory" ? "分桶" : FormatSize(Size ?? 0L);
    public string ModifiedDisplay => ModifiedAt?.ToLocalTime().ToString("yyyy-MM-dd HH:mm") ?? "—";

    private static string FormatSize(long bytes)
    {
        if (bytes >= 1024L * 1024 * 1024) return $"{bytes / (1024d * 1024 * 1024):N1} GB";
        if (bytes >= 1024L * 1024) return $"{bytes / (1024d * 1024):N1} MB";
        return $"{bytes / 1024d:N1} KB";
    }
}
