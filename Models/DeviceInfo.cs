namespace HarmonyFileManager.Models;

public sealed record DeviceInfo(string Id, string Status)
{
    /// <summary>该设备是否已建立目标应用(bftpd)维护通道,由主窗口在连接状态变化时刷新。
    /// 绿点=通道就绪可维护沙箱;红点=设备在但通道未建立(未安装目标应用/离线/连接失败)。</summary>
    public bool ChannelReady { get; init; }

    public string DisplayName => $"{Id}  ·  {StatusDisplay}";

    /// <summary>悬停提示:解释红/绿点含义,帮助判断为何设备在列但通道不可用。</summary>
    public string ChannelTip => ChannelReady
        ? "维护通道已就绪（绿点）：设备已建立目标应用 bftpd 通道，可操作沙箱文件。"
        : "维护通道未就绪（红点）：设备可能未安装目标应用、离线、未授权或连接失败，文件操作不可用。可点击「刷新设备」重试。";

    /// <summary>hdc list targets 的状态为英文(device/offline/unauthorized),转成中文展示。
    /// 实测:该 hdc 版本对正常连接的调试设备也输出 unknown,且实际可正常操作,故 unknown 视为已连接。</summary>
    private string StatusDisplay => Status switch
    {
        "device" => "已连接",
        "unknown" => "已连接",
        "offline" => "离线",
        "unauthorized" => "未授权",
        _ => Status,
    };
}