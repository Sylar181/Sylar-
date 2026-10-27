using System.Diagnostics;
using System.IO;
using System.Text;
using System.Text.Json;
using HarmonyFileManager.Models;

namespace HarmonyFileManager.Services;

/// <summary>封装本机 hdc.exe 的设备发现、shell、端口转发与文件桥接能力。</summary>
public sealed class HdcService
{
    private readonly string _hdcPath;

    public HdcService(string hdcPath) => _hdcPath = hdcPath;

    /// <summary>配置的 hdc.exe 当前是否存在(供界面在缺失时提示引导安装)。</summary>
    public bool IsAvailable => File.Exists(_hdcPath);

    public async Task<IReadOnlyList<DeviceInfo>> ListDevicesAsync(CancellationToken cancellationToken)
    {
        var output = await RunAsync(cancellationToken, "list", "targets");
        // hdc list targets 无设备时输出占位符 "[Empty]",必须过滤,否则会被当成一台真实设备显示并触发自动连接。
        // 正常输出为 "设备ID<Tab/空格>状态",如 "2MF0224716000990 device"。
        return output.Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries)
            .Select(line => line.Trim().TrimStart('-', ' '))
            .Where(line => !string.IsNullOrWhiteSpace(line))
            .Select(line =>
            {
                var parts = line.Split(new[] { ' ', '\t' }, StringSplitOptions.RemoveEmptyEntries);
                var id = parts[0].Trim();
                var status = parts.Length > 1 ? parts[1].Trim().ToLowerInvariant() : "unknown";
                return new DeviceInfo(id, status);
            })
            .Where(d => !string.IsNullOrWhiteSpace(d.Id) && !d.Id.StartsWith('[')) // 过滤 [Empty] 等占位符
            .ToList();
    }

    /// <summary>在指定设备上执行一条 shell 命令(管道、引号按设备端 shell 规则解释)。可指定更短超时(秒),用于快速清理类命令。</summary>
    public Task<string> RunShellAsync(string deviceId, string command, CancellationToken cancellationToken, int? timeoutSeconds = null)
    {
        var arguments = new[] { "-t", deviceId, "shell", command };
        return timeoutSeconds is int timeout
            ? RunAsyncWithTimeout(cancellationToken, timeout, arguments)
            : RunAsync(cancellationToken, arguments);
    }

    /// <summary>查询设备上指定 bundle 的已安装版本(bm dump -n)。返回 (versionName, versionCode);
    /// 应用未安装、命令失败或无法解析时返回 null。不依赖 bftpd 沙箱通道,设备在线即可查询。</summary>
    public async Task<(string VersionName, long VersionCode)?> GetInstalledVersionAsync(string deviceId, string bundleName, CancellationToken cancellationToken)
    {
        try
        {
            var output = await RunShellAsync(deviceId, $"bm dump -n {bundleName}", cancellationToken, timeoutSeconds: 15);
            // bm dump 输出为 JSON,取最外层花括号包裹的根对象
            var start = output.IndexOf('{');
            var end = output.LastIndexOf('}');
            if (start < 0 || end <= start) return null;
            using var document = JsonDocument.Parse(output[start..(end + 1)]);
            var root = document.RootElement;
            if (root.TryGetProperty("versionName", out var nameElement)
                && root.TryGetProperty("versionCode", out var codeElement))
            {
                return (nameElement.GetString() ?? string.Empty, codeElement.GetInt64());
            }
            return null;
        }
        catch
        {
            // 设备未安装该应用/权限不足/命令失败,统一视为查不到
            return null;
        }
    }

    /// <summary>以目标应用身份启动 bftpd FTP 服务(aa process ... -S)。
    /// 整个命令作为单个 shell 字符串传递,避免逐参数传递时引号嵌套导致 aa process 挂起。</summary>
    public Task<string> StartFtpServerAsync(string deviceId, ManagedAppPolicy app, string bftpdCommand, CancellationToken cancellationToken)
    {
        var shellCommand = $"aa process -b {app.BundleName} -a {app.AbilityName} -p \"{bftpdCommand}\" -S";
        return RunShellAsync(deviceId, shellCommand, cancellationToken);
    }

    public Task<string> StopFtpServerAsync(string deviceId, CancellationToken cancellationToken)
        => RunShellAsync(deviceId, "pkill -x bftpd 2>/dev/null", cancellationToken);

    public async Task<bool> IsFtpServerRunningAsync(string deviceId, int port, CancellationToken cancellationToken)
    {
        var output = await RunShellAsync(deviceId, "ps -ef | grep bftpd", cancellationToken);
        var lines = output.Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries);
        var pattern = $"-p {port}";
        return lines.Any(line => line.Contains("bftpd", StringComparison.OrdinalIgnoreCase)
                                 && line.Contains(pattern, StringComparison.OrdinalIgnoreCase)
                                 && !line.Contains("grep", StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>把设备端口转发到本机 127.0.0.1 同名端口,便于 Windows 端直连。</summary>
    public async Task<string> ForwardPortAsync(string deviceId, int port, CancellationToken cancellationToken)
    {
        var spec = $"tcp:{port}";
        try { await RunAsync(cancellationToken, "-t", deviceId, "fport", "rm", spec); }
        catch { /* 无旧转发时忽略 */ }
        return await RunAsync(cancellationToken, "-t", deviceId, "fport", spec, spec);
    }

    public Task<string> RemoveForwardAsync(string deviceId, int port, CancellationToken cancellationToken)
        => RunAsync(cancellationToken, "-t", deviceId, "fport", "rm", $"tcp:{port}");

    /// <summary>把本机文件推到设备临时目录(hdc file send)。大文件传输使用更长超时。</summary>
    public Task<string> SendFileAsync(string deviceId, string localPath, string remotePath, CancellationToken cancellationToken)
        => RunAsyncWithTimeout(cancellationToken, FileTransferTimeoutSeconds, "-t", deviceId, "file", "send", localPath, remotePath);

    /// <summary>把设备临时文件拉到本机(hdc file recv)。大文件传输使用更长超时。</summary>
    public Task<string> ReceiveFileAsync(string deviceId, string remotePath, string localPath, CancellationToken cancellationToken)
        => RunAsyncWithTimeout(cancellationToken, FileTransferTimeoutSeconds, "-t", deviceId, "file", "recv", remotePath, localPath);

    /// <summary>通过 hdc 官方应用安装通道把本地 .hap/.app 安装包安装到指定设备(独立于沙箱 FTP 维护)。
    /// overwrite 为 true 时追加 -r 覆盖更新同名应用。安装包较大,使用文件传输级超时。</summary>
    public async Task<string> InstallPackageAsync(string deviceId, string localPackagePath, bool overwrite, CancellationToken cancellationToken)
    {
        if (!File.Exists(localPackagePath))
        {
            throw new FileNotFoundException("未找到安装包文件。", localPackagePath);
        }
        var arguments = new List<string> { "-t", deviceId, "install" };
        if (overwrite) arguments.Add("-r");
        arguments.Add(localPackagePath);
        return await RunAsyncWithTimeout(cancellationToken, FileTransferTimeoutSeconds, arguments.ToArray());
    }

    private Task<string> RunAsync(CancellationToken cancellationToken, params string[] arguments)
        => RunAsyncCore(cancellationToken, CommandTimeoutSeconds, arguments);

    private Task<string> RunAsyncWithTimeout(CancellationToken cancellationToken, int timeoutSeconds, params string[] arguments)
        => RunAsyncCore(cancellationToken, timeoutSeconds, arguments);

    private async Task<string> RunAsyncCore(CancellationToken cancellationToken, int timeoutSeconds, string[] arguments)
    {
        if (!File.Exists(_hdcPath))
        {
            throw new FileNotFoundException("未找到 hdc.exe，请在 appsettings.json 中配置 HdcPath。", _hdcPath);
        }

        // 不设全局串行:设备端 bftpd 与 hdc 均支持并发连接(实测上传与列表可同时执行)。
        // 全局串行会导致大文件上传占用期间浏览/其他操作全部排队,表现为“看不见文件夹”。
        var startInfo = new ProcessStartInfo(_hdcPath)
        {
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true,
            // 设备端输出为 UTF-8 字节,必须显式用 UTF-8 读取,否则中文文件名会变成乱码(被 GBK/ANSI 解读)
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8
        };
        foreach (var argument in arguments) startInfo.ArgumentList.Add(argument);

        using var process = Process.Start(startInfo) ?? throw new InvalidOperationException("无法启动 HDC。");
        // 手动读取原始字节并强制按 UTF-8 解码,不依赖 StandardOutputEncoding(可能受系统 ANSI 编码影响导致中文乱码)
        var outputTask = ReadAllTextUtf8Async(process.StandardOutput.BaseStream, cancellationToken);
        var errorTask = ReadAllTextUtf8Async(process.StandardError.BaseStream, cancellationToken);
        // 带超时等待退出,防止 hdc 命令挂起导致界面卡死
        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeoutCts.CancelAfter(TimeSpan.FromSeconds(timeoutSeconds));
        try
        {
            await process.WaitForExitAsync(timeoutCts.Token);
        }
        catch (OperationCanceledException)
        {
            // 无论超时还是被取消,都先终止 Windows 端 hdc 进程,避免残留占用端口或堆积连接
            try { process.Kill(entireProcessTree: true); } catch { /* 忽略 */ }
            if (cancellationToken.IsCancellationRequested) throw; // 用户/调用方取消,原样上抛
            throw new TimeoutException($"hdc 命令执行超时({timeoutSeconds} 秒),已自动终止。");
        }
        var output = await outputTask;
        var error = await errorTask;
        if (process.ExitCode != 0) throw new InvalidOperationException(string.IsNullOrWhiteSpace(error) ? output : error);
        return output;
    }

    /// <summary>读取流中的原始字节并强制按 UTF-8 解码(设备端输出为 UTF-8)。</summary>
    private static async Task<string> ReadAllTextUtf8Async(Stream stream, CancellationToken cancellationToken)
    {
        using var buffer = new MemoryStream();
        await stream.CopyToAsync(buffer, cancellationToken);
        return Encoding.UTF8.GetString(buffer.ToArray());
    }

    private const int CommandTimeoutSeconds = 30;
    private const int FileTransferTimeoutSeconds = 300;
}
