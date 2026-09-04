using System.Diagnostics;
using System.IO;
using System.Windows;
using HarmonyFileManager.Models;
using HarmonyFileManager.Services;
using Microsoft.Win32;

namespace HarmonyFileManager;

/// <summary>首次启动或 hdc 缺失时的工具链引导窗口:自动下载、选择压缩包安装、手动指定或打开官方页。</summary>
public partial class HdcSetupWindow : Window
{
    private readonly HdcInstallOptions? _options;

    /// <summary>安装/指定成功后返回的 hdc.exe 完整路径。</summary>
    public string? SelectedHdcPath { get; private set; }

    public HdcSetupWindow(HdcInstallOptions? options)
    {
        InitializeComponent();
        _options = options;
        AutoDownloadButton.IsEnabled = !string.IsNullOrWhiteSpace(options?.DownloadUrl);
        AutoDownloadButton.ToolTip = string.IsNullOrWhiteSpace(options?.DownloadUrl)
            ? "未配置 HdcInstall.DownloadUrl(需指向含 hdc.exe 的压缩包直链)。可改用“选择压缩包并安装”或“手动指定 hdc.exe”。"
            : null;
        OpenDownloadPageButton.Visibility = string.IsNullOrWhiteSpace(options?.DownloadPage) ? Visibility.Collapsed : Visibility.Visible;
        OpenUsbDriverButton.Visibility = string.IsNullOrWhiteSpace(options?.UsbDriverPage) ? Visibility.Collapsed : Visibility.Visible;

        var found = HdcToolchainLocator.Resolve(null);
        SummaryText.Text = found != null
            ? $"已在本机探测到:{found}\n可关闭本窗口直接使用;如需更换路径,也可在此重新安装或指定。"
            : "未在本机探测到可用的 hdc.exe,请选择一种方式完成安装或指定。";
    }

    private async void AutoDownload_Click(object sender, RoutedEventArgs e)
    {
        var url = _options?.DownloadUrl;
        if (string.IsNullOrWhiteSpace(url)) return;
        await RunInstallAsync(() => HdcToolchainInstaller.DownloadAndInstallAsync(url, CreateProgress(), CancellationToken.None));
    }

    private async void InstallZip_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFileDialog { Title = "选择包含 hdc.exe 的压缩包", Filter = "压缩包 (*.zip)|*.zip" };
        if (dialog.ShowDialog(this) != true) return;
        var archive = dialog.FileName;
        await RunInstallAsync(() => HdcToolchainInstaller.InstallFromArchiveAsync(archive, CreateProgress(), CancellationToken.None));
    }

    private async void BrowseExe_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFileDialog { Title = "选择 hdc.exe", Filter = "hdc 可执行文件 (hdc.exe)|hdc.exe" };
        if (dialog.ShowDialog(this) != true) return;
        var path = dialog.FileName;
        SetBusy(true);
        try
        {
            AppendLog($"正在校验:{path}");
            if (!await HdcToolchainInstaller.VerifyAsync(path, CancellationToken.None))
            {
                AppendLog("校验失败:该文件无法以 hdc 方式运行,请确认选择的是 HarmonyOS/OpenHarmony 的 hdc.exe。");
                return;
            }
            AppendLog($"已指定:{path}");
            SelectedHdcPath = path;
            DialogResult = true;
        }
        finally
        {
            SetBusy(false);
        }
    }

    private async Task RunInstallAsync(Func<Task<string?>> install)
    {
        SetBusy(true);
        try
        {
            var path = await install();
            if (string.IsNullOrEmpty(path))
            {
                AppendLog("安装未完成:未能定位 hdc.exe,请换一个压缩包或手动指定。");
                return;
            }
            AppendLog($"安装完成:{path}");
            SelectedHdcPath = path;
            DialogResult = true;
        }
        catch (Exception exception)
        {
            AppendLog($"安装失败:{exception.Message}");
        }
        finally
        {
            SetBusy(false);
        }
    }

    private void OpenDownloadPage_Click(object sender, RoutedEventArgs e) => OpenInBrowser(_options?.DownloadPage);

    private void OpenUsbDriver_Click(object sender, RoutedEventArgs e) => OpenInBrowser(_options?.UsbDriverPage);

    private void Close_Click(object sender, RoutedEventArgs e) => Close();

    private IProgress<string> CreateProgress() => new Progress<string>(AppendLog);

    private void SetBusy(bool busy)
    {
        IsEnabled = !busy;
        ProgressPanel.Visibility = busy ? Visibility.Visible : Visibility.Collapsed;
    }

    private void AppendLog(string message)
    {
        LogText.Text = LogText.Text.Length == 0 ? message : $"{LogText.Text}\n{message}";
        LogScroll.ScrollToEnd();
    }

    private static void OpenInBrowser(string? url)
    {
        if (string.IsNullOrWhiteSpace(url)) return;
        try
        {
            Process.Start(new ProcessStartInfo(url) { UseShellExecute = true });
        }
        catch (Exception exception)
        {
            MessageBox.Show(exception.Message, "无法打开网页", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }
}
