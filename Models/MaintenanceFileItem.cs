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

public sealed class TransferProgress
{
    public required string TransferId { get; init; }
    public required string Direction { get; init; }
    public required string FileName { get; init; }
    public long BytesTransferred { get; init; }
    public long TotalBytes { get; init; }
    public string State { get; init; } = "queued";
    public double Progress => TotalBytes == 0 ? 0 : Math.Round(BytesTransferred * 100d / TotalBytes, 1);
}