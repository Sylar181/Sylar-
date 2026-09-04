namespace HarmonyFileManager.Models;

/// <summary>HDC 工具链引导安装配置(HdcPath 缺失时的兜底来源)。</summary>
public sealed class HdcInstallOptions
{
    /// <summary>自动下载源:指向含 hdc.exe 的压缩包直链(一般由企业内网镜像或官方渠道提供)。为空时引导手动安装。</summary>
    public string? DownloadUrl { get; init; }

    /// <summary>官方下载页,供“打开下载页”按钮使用。</summary>
    public string? DownloadPage { get; init; }

    /// <summary>USB 驱动说明页,供设备无法被识别时参考。</summary>
    public string? UsbDriverPage { get; init; }
}
