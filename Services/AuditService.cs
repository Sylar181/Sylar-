using System.Text;
using System.IO;

namespace HarmonyFileManager.Services;

public sealed class AuditService
{
    private readonly string _logPath = Path.Combine(AppContext.BaseDirectory, "logs", "audit.log");

    public async Task WriteAsync(string action, string detail)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(_logPath)!);
        var line = $"{DateTimeOffset.Now:O}\t{action}\t{detail}{Environment.NewLine}";
        await File.AppendAllTextAsync(_logPath, line, Encoding.UTF8);
    }
}