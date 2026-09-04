namespace HarmonyFileManager.Models;

/// <summary>设备端 bftpd FTP 通道的接入参数。</summary>
public sealed class FtpAccessOptions
{
    /// <summary>bftpd 监听端口(本地端口转发后与之一致)。</summary>
    public required int Port { get; init; }

    /// <summary>FTP 登录用户名(匿名即可,服务以应用身份运行)。</summary>
    public required string UserName { get; init; }

    /// <summary>FTP 登录密码。</summary>
    public required string Password { get; init; }

    /// <summary>以应用身份启动 bftpd 的命令,例如 "/system/bin/bftpd -D -p 8000"。</summary>
    public required string BftpdCommand { get; init; }
}
