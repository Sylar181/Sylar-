using System.Globalization;
using System.IO;
using System.Net.Sockets;
using System.Text;
using System.Text.RegularExpressions;
using HarmonyFileManager.Models;

namespace HarmonyFileManager.Services;

/// <summary>
/// 通过 hdc 以目标应用身份启动设备端 bftpd FTP 服务,再访问其受控沙箱目录。
/// 列表/下载/上传走设备端 ftpget/ftpput + hdc file 桥接,删除/重命名走 FTP 控制通道(仅需端口转发)。
/// 所有远程路径在操作前均经过 <see cref="PathPolicyService"/> 白名单与敏感路径校验。
/// </summary>
public sealed class FtpFileService
{
    private static readonly Regex UnixListRegex = new(
        @"^(?<perm>[d\-lbcsp])(?<mode>[rwxstST\-]{9})\s+\d+\s+\S+\s+\S+\s+(?<size>\d+)\s+(?<month>[A-Za-z]{3})\s+(?<day>\d{1,2})\s+(?<time>\d{1,2}:\d{2}|\d{4})\s+(?<name>.+)$",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    private readonly HdcService _hdc;
    private readonly FtpAccessOptions _ftp;
    private readonly ManagedAppPolicy _app;
    private readonly PathPolicyService _pathPolicy;
    private readonly AuditService _audit;
    private readonly string _deviceId;
    private int _activePort;
    private bool _connected;
    private readonly SemaphoreSlim _ftpLock = new(1, 1);
    private readonly SemaphoreSlim _connectLock = new(1, 1);

    public FtpFileService(HdcService hdc, FtpAccessOptions ftp, ManagedAppPolicy app, PathPolicyService pathPolicy, AuditService audit, string deviceId)
    {
        _hdc = hdc;
        _ftp = ftp;
        _app = app;
        _pathPolicy = pathPolicy;
        _audit = audit;
        _deviceId = deviceId;
        _activePort = ftp.Port;
    }

    public bool IsConnected => _connected;

    /// <summary>当前连接的设备 ID(供 UI 判断设备是否已断开)。</summary>
    public string DeviceId => _deviceId;

    /// <summary>串行化所有 FTP 操作。所有公共方法各自持锁一次,内部递归全部走无锁版本(不嵌套获取锁),
    /// 因此无需可重入——纯信号量,不依赖 AsyncLocal,杜绝跨 await 边界锁泄漏。等待锁超过 10 秒视为异常。</summary>
    private async Task AcquireFtpLockAsync(CancellationToken cancellationToken)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(10));
        try
        {
            await _ftpLock.WaitAsync(timeout.Token);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            throw new TimeoutException("FTP 操作繁忙，请稍后重试。");
        }
    }

    private void ReleaseFtpLock() => _ftpLock.Release();

    /// <summary>确保设备端 bftpd 已运行、端口已转发。不建立常驻 FTP 控制连接——列表/下载/上传走设备端命令,
    /// 删除/重命名按需建立一次性短连接,用完即断,避免 bftpd 连接槽被常驻连接占用导致后续操作挂起。</summary>
    public async Task ConnectAsync(CancellationToken cancellationToken)
    {
        await _connectLock.WaitAsync(cancellationToken);
        try
        {
            // 清理设备端残留:挂起的 FTP 客户端、所有旧 bftpd、挂起的 aa process 启动命令
            // (pkill 对应用身份 bftpd 可能权限不足,尽力而为;aa process/ftpget/ftpput 为 shell 身份可杀)
            try { await _hdc.RunShellAsync(_deviceId, "pkill -x ftpput 2>/dev/null; pkill -x ftpget 2>/dev/null; pkill -f \"aa process\" 2>/dev/null; pkill -x bftpd 2>/dev/null; sleep 0.3", cancellationToken); } catch { /* 忽略 */ }
            _activePort = await FindAvailablePortAndStartAsync(cancellationToken);
            _connected = true;
            await _audit.WriteAsync("ftp-connect", $"{_app.BundleName} @ {_deviceId} :{_activePort}");
        }
        finally { _connectLock.Release(); }
    }

    public async Task DisconnectAsync(CancellationToken cancellationToken)
    {
        _connected = false;
        try { await _hdc.RunShellAsync(_deviceId, "pkill -x ftpput 2>/dev/null; pkill -x ftpget 2>/dev/null", CancellationToken.None, timeoutSeconds: 5); } catch { /* 忽略 */ }
        try { await _hdc.StopFtpServerAsync(_deviceId, cancellationToken); } catch { /* 忽略 */ }
        await _audit.WriteAsync("ftp-disconnect", _app.BundleName);
    }

    /// <summary>列出沙箱目录(设备端 ftpget -l),返回带完整沙箱路径的条目。
    /// 列表为只读操作,不获取 FTP 锁——即使正在上传/下载大文件,浏览也不被阻塞(bftpd 与 hdc 支持并发)。</summary>
    public async Task<IReadOnlyList<MaintenanceFileItem>> ListFilesAsync(string remoteDir, CancellationToken cancellationToken)
    {
        return await ListFilesCoreAsync(remoteDir, cancellationToken);
    }

    private async Task<IReadOnlyList<MaintenanceFileItem>> ListFilesCoreAsync(string remoteDir, CancellationToken cancellationToken)
    {
        _pathPolicy.EnsureAllowed(_app, remoteDir);
        var output = await RunDeviceCommandWithRetryAsync($"ftpget -l -p {_activePort} -P {_ftp.Password} -u {_ftp.UserName} localhost {DevicePathExpr(remoteDir)}", "ftpget", cancellationToken);
        return ParseListing(output, remoteDir);
    }

    /// <summary>把路径编码为设备端 shell 可解码的表达式。hdc 传递参数时中文会变成“?”,
    /// 因此路径统一用 UTF-8 base64(纯 ASCII)编码,设备端 shell 用 base64 -d 还原,确保中文文件名/目录名可靠。</summary>
    private static string DevicePathExpr(string path)
    {
        var base64 = Convert.ToBase64String(System.Text.Encoding.UTF8.GetBytes(path));
        return $"\"$(echo {base64} | base64 -d)\"";
    }

    /// <summary>执行设备端 FTP 客户端命令(ftpget/ftpput);若命令挂起超时或被取消,自动清理设备端残留进程,避免其占住 bftpd 连接导致后续操作全部挂起。耗时异常时记录诊断。</summary>
    private async Task<string> RunDeviceCommandAsync(string command, string clientName, CancellationToken cancellationToken, int timeoutSeconds = 10)
    {
        var stopwatch = System.Diagnostics.Stopwatch.StartNew();
        try
        {
            var result = await _hdc.RunShellAsync(_deviceId, command, cancellationToken, timeoutSeconds: timeoutSeconds);
            stopwatch.Stop();
            if (stopwatch.ElapsedMilliseconds > 3000)
            {
                await _audit.WriteAsync("slow-device-cmd", $"{clientName} {stopwatch.ElapsedMilliseconds}ms :: {command}");
            }
            return result;
        }
        catch (Exception exception) when (exception is TimeoutException or OperationCanceledException)
        {
            stopwatch.Stop();
            await _audit.WriteAsync("device-cmd-abort", $"{clientName} {stopwatch.ElapsedMilliseconds}ms {exception.GetType().Name} :: {command}");
            try { await _hdc.RunShellAsync(_deviceId, $"pkill -x {clientName} 2>/dev/null", CancellationToken.None, timeoutSeconds: 5); } catch { /* 忽略 */ }
            throw;
        }
    }

    /// <summary>执行设备端命令;若挂起超时(多为 bftpd 被挂起连接污染),自动重连到全新 bftpd 并重试一次。</summary>
    private async Task<string> RunDeviceCommandWithRetryAsync(string command, string clientName, CancellationToken cancellationToken, int timeoutSeconds = 5)
    {
        for (var attempt = 0; ; attempt++)
        {
            try
            {
                return await RunDeviceCommandAsync(command, clientName, cancellationToken, timeoutSeconds);
            }
            catch (TimeoutException) when (attempt == 0)
            {
                await _audit.WriteAsync("device-retry", $"{clientName} :: {command}");
                try { await ConnectAsync(cancellationToken); } catch { /* 忽略 */ }
            }
        }
    }

    /// <summary>下载:设备端 ftpget 拉到设备临时目录,再由 hdc file recv 拉回本机。</summary>
    /// <summary>下载(Windows 端 FTP RETR)。</summary>
    public async Task DownloadAsync(string remotePath, string localPath, CancellationToken cancellationToken)
    {
        await AcquireFtpLockAsync(cancellationToken);
        try
        {
            await DownloadCoreAsync(remotePath, localPath, cancellationToken);
        }
        finally { ReleaseFtpLock(); }
    }

    private async Task DownloadCoreAsync(string remotePath, string localPath, CancellationToken cancellationToken)
    {
        _pathPolicy.EnsureAllowed(_app, remotePath);
        var temp = $"/data/local/tmp/hfm_dl_{Guid.NewGuid():N}.tmp";
        try
        {
            // 先查远程文件大小(用于下载完整性校验)
            var remoteSize = await GetRemoteFileSizeAsync(remotePath, cancellationToken);
            // 设备端 ftpget 也存在“数据写完但进程不退出”的情况:短超时后直接 file recv(temp 数据通常已完整)
            try
            {
                await RunDeviceCommandAsync($"ftpget -p {_activePort} -P {_ftp.Password} -u {_ftp.UserName} localhost {Quote(temp)} {DevicePathExpr(remotePath)}", "ftpget", cancellationToken, timeoutSeconds: 3);
            }
            catch (TimeoutException)
            {
                await _audit.WriteAsync("download-retry", remotePath);
                await ConnectAsync(cancellationToken);
            }
            await _hdc.ReceiveFileAsync(_deviceId, temp, localPath, cancellationToken);
            // 校验本地文件大小与远程一致,避免拿到不完整的下载结果
            var localSize = new FileInfo(localPath).Length;
            if (remoteSize >= 0 && localSize != remoteSize)
            {
                throw new IOException($"下载不完整:{localSize}/{remoteSize} 字节");
            }
            await _audit.WriteAsync("file-download", $"{remotePath} -> {localPath}");
        }
        finally
        {
            try { await _hdc.RunShellAsync(_deviceId, $"rm -f {Quote(temp)}", CancellationToken.None); } catch { /* 忽略 */ }
        }
    }

    /// <summary>通过父目录列表获取远程文件大小;失败或非文件返回 -1。</summary>
    private async Task<long> GetRemoteFileSizeAsync(string remotePath, CancellationToken cancellationToken)
    {
        try
        {
            var slash = remotePath.LastIndexOf('/');
            if (slash < 0) return -1;
            var parent = remotePath[..slash];
            var fileName = remotePath[(slash + 1)..];
            var output = await RunDeviceCommandAsync($"ftpget -l -p {_activePort} -P {_ftp.Password} -u {_ftp.UserName} localhost {DevicePathExpr(parent)}", "ftpget", cancellationToken, timeoutSeconds: 5);
            foreach (var rawLine in output.Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries))
            {
                var line = rawLine.Trim();
                var match = UnixListRegex.Match(line);
                if (match.Success && match.Groups["name"].Value == fileName)
                {
                    return long.Parse(match.Groups["size"].Value, CultureInfo.InvariantCulture);
                }
            }
            return -1;
        }
        catch { return -1; }
    }

    /// <summary>递归下载远程目录到本地目录(自动建本地目录结构)。持锁一次,内部递归全部走无锁版本。</summary>
    public async Task DownloadFolderAsync(string remoteDir, string localDir, CancellationToken cancellationToken)
    {
        await AcquireFtpLockAsync(cancellationToken);
        try
        {
            await DownloadFolderCoreAsync(remoteDir, localDir, cancellationToken);
            await _audit.WriteAsync("folder-download", $"{remoteDir} -> {localDir}");
        }
        finally { ReleaseFtpLock(); }
    }

    private async Task DownloadFolderCoreAsync(string remoteDir, string localDir, CancellationToken cancellationToken)
    {
        _pathPolicy.EnsureAllowed(_app, remoteDir);
        Directory.CreateDirectory(localDir);
        var items = await ListFilesCoreAsync(remoteDir, cancellationToken);
        foreach (var item in items)
        {
            var localPath = Path.Combine(localDir, SanitizeFileName(item.Name));
            if (item.Type == "directory")
            {
                await DownloadFolderCoreAsync(item.Path, localPath, cancellationToken);
            }
            else
            {
                await DownloadCoreAsync(item.Path, localPath, cancellationToken);
            }
        }
    }

    private static string SanitizeFileName(string name)
    {
        foreach (var invalid in Path.GetInvalidFileNameChars()) name = name.Replace(invalid, '_');
        return name;
    }

    /// <summary>上传(Windows 端 FTP STOR),不依赖设备端 ftpput。</summary>
    public async Task UploadAsync(string localPath, string remotePath, CancellationToken cancellationToken)
    {
        await AcquireFtpLockAsync(cancellationToken);
        try
        {
            await UploadCoreAsync(localPath, remotePath, cancellationToken);
        }
        finally { ReleaseFtpLock(); }
    }

    private async Task UploadCoreAsync(string localPath, string remotePath, CancellationToken cancellationToken)
    {
        _pathPolicy.EnsureAllowed(_app, remotePath);
        var temp = $"/data/local/tmp/hfm_up_{Guid.NewGuid():N}.tmp";
        var localSize = new FileInfo(localPath).Length;
        try
        {
            await _hdc.SendFileAsync(_deviceId, localPath, temp, cancellationToken);
            // 设备端 ftpput 传输不可靠:可能中途卡死或写完后进程不退出,且会污染 bftpd。
            // 策略:后台启动 ftpput(长超时不中断),轮询目标文件大小;检测到“卡住”(大小长时间不变或校验挂起)即重连到新 bftpd 重试,最多 3 次。
            var complete = false;
            Task<string>? ftpputTask = null;
            for (var attempt = 0; attempt < 3 && !complete; attempt++)
            {
                ftpputTask = RunDeviceCommandAsync($"ftpput -s -p {_activePort} -P {_ftp.Password} -u {_ftp.UserName} localhost {Quote(temp)} {DevicePathExpr(remotePath)}", "ftpput", cancellationToken, timeoutSeconds: 180);
                var lastSize = -2L;
                var stagnantRounds = 0;
                for (var i = 0; i < 90; i++)
                {
                    if (ftpputTask.IsCompleted && !complete)
                    {
                        try { await ftpputTask; } catch { /* 记录原始错误 */ }
                        break;
                    }
                    var size = await GetRemoteFileSizeAsync(remotePath, cancellationToken);
                    if (size == localSize)
                    {
                        // 双重确认:等 300ms 再查一次,避免读到瞬时/缓存值(未完全落盘)
                        await Task.Delay(300, cancellationToken);
                        if (await GetRemoteFileSizeAsync(remotePath, cancellationToken) == localSize)
                        {
                            complete = true;
                            break;
                        }
                    }
                    if (size == lastSize) stagnantRounds++;
                    else { lastSize = size; stagnantRounds = 0; }
                    if (stagnantRounds >= 10) break; // 大小约 3 秒无变化:传输卡住,重连重试
                    if (!cancellationToken.IsCancellationRequested) await Task.Delay(300, cancellationToken);
                }
                if (!complete)
                {
                    await _audit.WriteAsync("upload-retry", $"{remotePath} (attempt {attempt + 1})");
                    _ = ObserveQuietlyAsync(ftpputTask);
                    await ConnectAsync(cancellationToken); // 杀掉卡住的 ftpput 并切换到全新 bftpd
                }
            }
            if (!complete) throw new IOException($"上传未完成:目标文件大小不完整({remotePath})");
            // 上传成功:仅后台清理可能挂起的 ftpput 进程(已实测正常完成的 ftpput 不污染 bftpd,
            // 无需强制重连;只有卡住/失败时才 ConnectAsync 换干净 bftpd,大幅减少每次上传的重连开销)
            if (ftpputTask != null) _ = ObserveQuietlyAsync(ftpputTask);
            await _audit.WriteAsync("file-upload", $"{localPath} -> {remotePath}");
        }
        finally
        {
            try { await _hdc.RunShellAsync(_deviceId, $"rm -f {Quote(temp)}", CancellationToken.None); } catch { /* 忽略 */ }
        }
    }

    /// <summary>通过父目录列表校验目标文件大小是否已完整写入(ftpget -l 稳定,不用于文件本身)。</summary>
    private async Task<bool> IsRemoteFileCompleteAsync(string remotePath, long expectedSize, CancellationToken cancellationToken)
    {
        try
        {
            var slash = remotePath.LastIndexOf('/');
            if (slash < 0) return false;
            var parent = remotePath[..slash];
            var fileName = remotePath[(slash + 1)..];
            var output = await RunDeviceCommandAsync($"ftpget -l -p {_activePort} -P {_ftp.Password} -u {_ftp.UserName} localhost {DevicePathExpr(parent)}", "ftpget", cancellationToken, timeoutSeconds: 5);
            foreach (var rawLine in output.Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries))
            {
                var line = rawLine.Trim();
                var match = UnixListRegex.Match(line);
                if (match.Success && match.Groups["name"].Value == fileName)
                {
                    return long.Parse(match.Groups["size"].Value, CultureInfo.InvariantCulture) == expectedSize;
                }
            }
            return false;
        }
        catch { return false; }
    }

    /// <summary>递归创建远程目录(设备端 ftpput -M 逐级创建,已存在则忽略)。</summary>
    public async Task EnsureRemoteDirectoryAsync(string remoteDir, CancellationToken cancellationToken)
    {
        await AcquireFtpLockAsync(cancellationToken);
        try
        {
            await EnsureRemoteDirectoryCoreAsync(remoteDir, cancellationToken);
        }
        finally { ReleaseFtpLock(); }
    }

    private async Task EnsureRemoteDirectoryCoreAsync(string remoteDir, CancellationToken cancellationToken)
    {
        _pathPolicy.EnsureAllowed(_app, remoteDir);
        var cumulative = string.Empty;
        foreach (var part in remoteDir.Split('/', StringSplitOptions.RemoveEmptyEntries))
        {
            cumulative += "/" + part;
            try
            {
                await RunDeviceCommandWithRetryAsync($"ftpput -M -p {_activePort} -P {_ftp.Password} -u {_ftp.UserName} localhost {DevicePathExpr(cumulative)}", "ftpput", cancellationToken);
            }
            catch (InvalidOperationException ex) when (IsDirectoryExistsError(ex.Message))
            {
                // 目录已存在,继续下一级
            }
        }
    }

    private static bool IsDirectoryExistsError(string message)
        => message.Contains("451", StringComparison.OrdinalIgnoreCase)
           || message.Contains("exists", StringComparison.OrdinalIgnoreCase)
           || message.Contains("exist", StringComparison.OrdinalIgnoreCase);

    /// <summary>递归上传本地文件夹到远程目录(自动逐级建目录)。持锁一次,内部递归全部走无锁版本。</summary>
    public async Task UploadFolderAsync(string localDir, string remoteDir, CancellationToken cancellationToken)
    {
        await AcquireFtpLockAsync(cancellationToken);
        try
        {
            await UploadFolderCoreAsync(localDir, remoteDir, cancellationToken);
            await _audit.WriteAsync("folder-upload", $"{localDir} -> {remoteDir}");
        }
        finally { ReleaseFtpLock(); }
    }

    private async Task UploadFolderCoreAsync(string localDir, string remoteDir, CancellationToken cancellationToken)
    {
        _pathPolicy.EnsureAllowed(_app, remoteDir);
        await EnsureRemoteDirectoryCoreAsync(remoteDir, cancellationToken);
        foreach (var file in Directory.GetFiles(localDir))
        {
            await UploadCoreAsync(file, $"{remoteDir}/{Path.GetFileName(file)}", cancellationToken);
        }
        foreach (var subDir in Directory.GetDirectories(localDir))
        {
            await UploadFolderCoreAsync(subDir, $"{remoteDir}/{Path.GetFileName(subDir)}", cancellationToken);
        }
    }

    /// <summary>删除(全设备端命令):文件用 ftpput -d;目录用 ftpget -l 识别后递归清空再 ftpput -D。不依赖端口转发与控制连接。</summary>
    public async Task DeleteAsync(IReadOnlyList<string> remotePaths, CancellationToken cancellationToken)
    {
        await AcquireFtpLockAsync(cancellationToken);
        try
        {
            // 清理设备端可能残留的挂起 ftpput,避免占住 bftpd 连接
            try { await _hdc.RunShellAsync(_deviceId, "pkill -x ftpput 2>/dev/null", CancellationToken.None, timeoutSeconds: 5); } catch { /* 忽略 */ }
            foreach (var path in remotePaths)
            {
                _pathPolicy.EnsureAllowed(_app, path);
                await DeletePathAsync(path, cancellationToken);
            }
            await _audit.WriteAsync("file-delete", string.Join(";", remotePaths));
            // ftpput -d/-D 会污染当前 bftpd:实测删除后紧接着的上传 ftpput 会卡住触发重试。
            // 删除完成后立即切换到干净 bftpd(干净实例上 ftpput 0.8s 完成),避免污染影响后续上传。
            await ConnectAsync(cancellationToken);
        }
        finally { ReleaseFtpLock(); }
    }

    private async Task DeletePathAsync(string path, CancellationToken cancellationToken)
    {
        // 用稳定的 ftpget -l 判断类型:能列出内容说明是目录;列表失败说明是文件或不存在
        IReadOnlyList<MaintenanceFileItem> items;
        try
        {
            items = await ListFilesCoreAsync(path, cancellationToken);
        }
        catch
        {
            // 是文件(或不存在):按文件删除
            await DeleteFileAsync(path, cancellationToken);
            return;
        }

        if (items.Count == 0)
        {
            // 文件或空目录无法通过列表区分。ftpput -d 对目录、ftpput -D 对文件都可能“假成功”(返回成功但实际不删),
            // 因此两个都执行:文件由 ftpput -d 删除,空目录由 ftpput -D 删除,互相兑底,确保真正删掉。
            await TryDeleteFileAsync(path, cancellationToken); // 文件:真删;目录:假成功或 550,无害
            await RmdirAsync(path, cancellationToken);         // 空目录:真删;文件:假成功或 451,无害
            return;
        }

        // 非空目录:递归删除所有内容(文件用 ftpput -d,子目录递归)
        foreach (var item in items)
        {
            if (item.Type == "directory")
            {
                await DeletePathAsync(item.Path, cancellationToken);
            }
            else
            {
                await DeleteFileAsync(item.Path, cancellationToken);
            }
        }

        await RmdirAsync(path, cancellationToken);
    }

    /// <summary>尝试按文件删除(ftpput -d)。返回 false 表示不是文件(目录或不存在)。</summary>
    private async Task<bool> TryDeleteFileAsync(string path, CancellationToken cancellationToken)
    {
        try
        {
            await RunDeviceCommandWithRetryAsync($"ftpput -d -p {_activePort} -P {_ftp.Password} -u {_ftp.UserName} localhost {DevicePathExpr(path)}", "ftpput", cancellationToken);
            return true;
        }
        catch (TimeoutException) { return false; }
        catch (InvalidOperationException ex) when (IsDirectoryExistsError(ex.Message)
                                                   || ex.Message.Contains("Is a directory", StringComparison.OrdinalIgnoreCase)
                                                   || ex.Message.Contains("Not a directory", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }
    }

    /// <summary>删除确认存在的文件(ftpput -d);失败则抛异常。</summary>
    private async Task DeleteFileAsync(string path, CancellationToken cancellationToken)
    {
        if (!await TryDeleteFileAsync(path, cancellationToken))
        {
            throw new IOException($"删除失败: {path}");
        }
    }

    /// <summary>删除空目录(ftpput -D);目录不存在则视为成功。</summary>
    private async Task RmdirAsync(string path, CancellationToken cancellationToken)
    {
        try
        {
            await RunDeviceCommandWithRetryAsync($"ftpput -D -p {_activePort} -P {_ftp.Password} -u {_ftp.UserName} localhost {DevicePathExpr(path)}", "ftpput", cancellationToken);
        }
        catch (InvalidOperationException ex) when (IsDirectoryExistsError(ex.Message))
        {
            // 目录不存在,视为成功
        }
    }
    public async Task RenameAsync(string remoteSource, string remoteTarget, CancellationToken cancellationToken)
    {
        await AcquireFtpLockAsync(cancellationToken);
        try
        {
            await RenameCoreAsync(remoteSource, remoteTarget, cancellationToken);
        }
        finally { ReleaseFtpLock(); }
    }

    private Task RenameCoreAsync(string remoteSource, string remoteTarget, CancellationToken cancellationToken)
    {
        // 设备端 bftpd 对匿名用户禁用 RNTO(451 Permission denied),且 ftpput -m 同样受限,
        // 重命名无法通过受控通道实现,明确抛出不支持。
        throw new NotSupportedException("设备端 bftpd 不允许重命名(权限限制),当前版本暂不支持此操作。");
    }

    private async Task<int> FindAvailablePortAndStartAsync(CancellationToken cancellationToken)
    {
        // 每次连接都启动全新 bftpd,绝不复用设备端可能状态不佳的旧实例。
        // 设备端已有 bftpd 的端口(含历史残留)一律跳过,确保每次连到的都是干净的新实例。
        for (var port = _ftp.Port; port < _ftp.Port + 50; port++)
        {
            if (!IsPortFree(port)) continue;
            // 设备端该端口已有 bftpd(旧实例)则跳过,保证用全新实例
            if (await _hdc.IsFtpServerRunningAsync(_deviceId, port, cancellationToken)) continue;
            try
            {
                await _hdc.StartFtpServerAsync(_deviceId, _app, BftpdCommandForPort(port), cancellationToken);
                // 等待 bftpd 启动后检查(固定等待比 40 次轮询更快)
                await Task.Delay(1200, cancellationToken);
                if (await _hdc.IsFtpServerRunningAsync(_deviceId, port, cancellationToken)) return port;
                await Task.Delay(800, cancellationToken);
                if (await _hdc.IsFtpServerRunningAsync(_deviceId, port, cancellationToken)) return port;
            }
            catch
            {
                // 该端口启动失败(可能被设备端残留进程占用),清理后尝试下一个
                try { await _hdc.RunShellAsync(_deviceId, "pkill -x bftpd 2>/dev/null", CancellationToken.None, timeoutSeconds: 5); } catch { /* 忽略 */ }
            }
        }
        throw new InvalidOperationException("无法启动 bftpd:可用端口尝试失败。");
    }

    private string BftpdCommandForPort(int port)
        => System.Text.RegularExpressions.Regex.Replace(_ftp.BftpdCommand, @"-p\s+\d+", $"-p {port}");

    private static bool IsPortFree(int port)
    {
        try
        {
            var listener = new System.Net.Sockets.TcpListener(System.Net.IPAddress.Loopback, port);
            listener.Start();
            listener.Stop();
            return true;
        }
        catch { return false; }
    }

    private const int FtpIoTimeoutSeconds = 15;

    private static IReadOnlyList<MaintenanceFileItem> ParseListing(string output, string remoteDir)
    {
        var items = new List<MaintenanceFileItem>();
        foreach (var rawLine in output.Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries))
        {
            var line = rawLine.Trim();
            // hdc shell 输出可能带 "-- " 前缀,去掉。
            if (line.StartsWith("--", StringComparison.Ordinal)) line = line[2..].TrimStart();
            if (line.Length == 0 || line.StartsWith("total ", StringComparison.Ordinal)) continue;

            var match = UnixListRegex.Match(line);
            if (!match.Success) continue;
            var name = match.Groups["name"].Value;
            if (name is "." or "..") continue;

            var isDirectory = match.Groups["perm"].Value == "d";
            var size = long.Parse(match.Groups["size"].Value, CultureInfo.InvariantCulture);
            var modified = ParseModified(match.Groups["month"].Value, match.Groups["day"].Value, match.Groups["time"].Value);

            items.Add(new MaintenanceFileItem
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

    private static DateTimeOffset? ParseModified(string month, string day, string time)
    {
        if (!DateTime.TryParse($"{month} {day} {time}", CultureInfo.InvariantCulture, DateTimeStyles.None, out var parsed)) return null;
        return new DateTimeOffset(parsed);
    }

    private static string Quote(string path) => $"\"{path.Replace("\"", "\\\"")}\"";

    /// <summary>观察后台任务结果并静默吞掉异常(用于不等待完成的 ftpput 任务)。</summary>
    private static async Task ObserveQuietlyAsync(Task task)
    {
        try { await task; } catch { /* 忽略 */ }
    }
}
