namespace HarmonyFileManager.Models;

public sealed class MaintenanceFileItem
{
    public required string Name { get; init; }
    public required string Type { get; init; }
    public string Path { get; init; } = string.Empty;
    public long? Size { get; init; }
    public DateTimeOffset? ModifiedAt { get; init; }
    public string SizeDisplay => Type == "directory" ? "文件夹" : $"{(Size ?? 0) / 1024d:N1} KB";
    public string ModifiedDisplay => ModifiedAt?.ToLocalTime().ToString("yyyy-MM-dd HH:mm") ?? "—";
}

public sealed record TransferProgress
{
    public required string TransferId { get; init; }
    public required string Direction { get; init; }
    public required string FileName { get; init; }
    public long BytesTransferred { get; init; }
    public long TotalBytes { get; init; }

    /// <summary>当前文件在本次批量传输中的序号(1 起);未知时为 0。</summary>
    public int FileIndex { get; init; }

    /// <summary>本次批量传输的文件总数;未知时为 0。</summary>
    public int FileCount { get; init; }

    /// <summary>当前阶段描述(如「推送到设备临时目录」「写入沙箱」「拉回本机」)。</summary>
    public string Stage { get; init; } = string.Empty;

    public string State { get; init; } = "queued";
    public double Progress => TotalBytes <= 0 ? 0 : Math.Round(BytesTransferred * 100d / TotalBytes, 1);
}