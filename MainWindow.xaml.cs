using System.IO;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using HarmonyFileManager.Models;
using HarmonyFileManager.Services;

namespace HarmonyFileManager;

public partial class MainWindow : Window
{
    private readonly ToolConfiguration _configuration;
    private readonly AuditService _auditService = new();
    private readonly PathPolicyService _pathPolicy = new();
    private HdcService _hdc = null!;
    private readonly DispatcherTimer _autoRefreshTimer;
    private FtpFileService? _ftp;
    private CancellationTokenSource? _loadCts;
    private string _currentPath = string.Empty;
    private IReadOnlyList<MaintenanceFileItem>? _lastFiles;
    private IReadOnlyList<string>? _lastDeviceIds;
    private bool _refreshing;
    private Point _dragStartPoint;
    private bool _draggingStarted;
    private IReadOnlyList<MaintenanceFileItem> _dragItems = [];
    private bool _isDragging;
    private bool _installingPackage; // 防止重复拖入/点击造成并发安装
    private readonly string _dragSaveFolder = Environment.GetFolderPath(Environment.SpecialFolder.Desktop);

    public MainWindow()
    {
        InitializeComponent();
        _configuration = LoadConfiguration();
        // 启动时自动探测 hdc:配置路径 → 工具自带/自动安装目录 → OpenHarmony/HarmonyOS SDK → DevEco Studio → PATH。
        // 探测不到时先以空路径启动,窗口加载完成后弹出引导(OnLoadedEnsureHdc),避免在窗口显示前弹模态框。
        var resolvedHdc = HdcToolchainLocator.Resolve(_configuration.HdcPath);
        _hdc = new HdcService(resolvedHdc ?? string.Empty);
        if (!string.IsNullOrEmpty(resolvedHdc)) PersistHdcPath(resolvedHdc);
        Loaded += OnLoadedEnsureHdc;
        PolicyNameText.Text = $"{_configuration.ManagedApplication.DisplayName}  ·  {_configuration.ManagedApplication.BundleName}";
        RootSelector.ItemsSource = _configuration.ManagedApplication.AllowedRoots.Keys.ToList();
        RootSelector.SelectedIndex = 0;
        SetFtpState(connected: false);
        StatusText.Text = _hdc.IsAvailable
            ? "每 5 秒自动检测设备；连接后自动刷新目录。"
            : "未找到 hdc.exe，正在准备引导安装…";
        _autoRefreshTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(5) };
        _autoRefreshTimer.Tick += AutoRefreshTick;
        _autoRefreshTimer.Start();
    }

    private void OnLoadedEnsureHdc(object sender, RoutedEventArgs e)
    {
        if (!_hdc.IsAvailable) OpenHdcSetup();
    }

    private void HdcSetup_Click(object sender, RoutedEventArgs e) => OpenHdcSetup();

    /// <summary>打开 HDC 引导窗口;成功后重建 HdcService 并刷新设备,把新路径写回 appsettings.json。</summary>
    private async void OpenHdcSetup()
    {
        var setup = new HdcSetupWindow(_configuration.HdcInstall);
        if (setup.ShowDialog() != true) return;
        var path = setup.SelectedHdcPath;
        if (string.IsNullOrWhiteSpace(path)) return;
        PersistHdcPath(path);
        _hdc = new HdcService(path);
        SetFtpState(connected: false);
        _ftp = null;
        StatusText.Text = "HDC 工具已就绪，正在检测设备…";
        await _auditService.WriteAsync("hdc-setup", path);
        await RefreshDevicesAsync(quiet: false);
    }

    /// <summary>把解析到的 hdc 路径写回运行目录 appsettings.json(保留其余配置);写失败不阻塞,下次启动自动探测兜底。</summary>
    private static void PersistHdcPath(string hdcPath)
    {
        var file = Path.Combine(AppContext.BaseDirectory, "appsettings.json");
        try
        {
            if (JsonNode.Parse(File.ReadAllText(file)) is not JsonObject root) return;
            var current = root["HdcPath"]?.GetValue<string>();
            if (string.Equals(current?.Trim(), hdcPath, StringComparison.OrdinalIgnoreCase)) return;
            root["HdcPath"] = hdcPath;
            File.WriteAllText(file, root.ToJsonString(new JsonSerializerOptions { WriteIndented = true }));
        }
        catch
        {
            // 忽略写回失败:本次运行已生效
        }
    }

    private async void RefreshDevices_Click(object sender, RoutedEventArgs e) => await RefreshDevicesAsync(quiet: false);

    /// <summary>打开独立的相册/视频下载窗口。与本窗口的文件管理逻辑完全独立。</summary>
    private void MediaDownload_Click(object sender, RoutedEventArgs e)
    {
        if (_configuration.MediaAccess is not { Enabled: true })
        {
            MessageBox.Show(this, "相册下载功能未启用，请在 appsettings.json 中配置 MediaAccess。", "提示", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }
        var window = new MediaWindow(_hdc, _auditService, _configuration.MediaAccess);
        window.Show();
    }

    private async Task RefreshDevicesAsync(bool quiet)
    {
        if (!_hdc.IsAvailable)
        {
            if (!quiet) StatusText.Text = "未找到 hdc.exe，请点击左侧「HDC 设置」安装或指定工具路径。";
            return;
        }
        try
        {
            if (!quiet) StatusText.Text = "正在检测 HDC 设备…";
            var devices = await _hdc.ListDevicesAsync(CancellationToken.None);
            var ids = devices.Select(device => device.Id).OrderBy(id => id, StringComparer.Ordinal).ToList();
            var changed = _lastDeviceIds == null || !ids.SequenceEqual(_lastDeviceIds);
            _lastDeviceIds = ids;

            if (!quiet || changed)
            {
                DevicesList.ItemsSource = MapChannelState(devices);
                if (devices.Count > 0) DevicesList.SelectedIndex = 0;
                // 列表有设备且尚未连接时,自动连接排第一台的设备
                if (_ftp?.IsConnected != true && devices.Count > 0)
                {
                    await ConnectToDeviceAsync(devices[0], quiet);
                }
            }

            if (!quiet)
            {
                await _auditService.WriteAsync("device-list", $"发现 {devices.Count} 台设备");
                StatusText.Text = devices.Count == 0 ? "未发现已授权设备。" : $"已发现 {devices.Count} 台已连接设备。";
            }
        }
        catch (Exception exception)
        {
            StatusText.Text = $"设备检测失败：{exception.Message}";
            await _auditService.WriteAsync("device-list-failed", exception.Message);
        }
    }

    /// <summary>按当前 FTP 连接状态给设备列表标注红/绿点:绿=该设备已建立维护通道。</summary>
    private List<DeviceInfo> MapChannelState(IEnumerable<DeviceInfo> devices)
    {
        var connectedId = _ftp?.IsConnected == true ? _ftp.DeviceId : null;
        return devices.Select(d => d with { ChannelReady = d.Id == connectedId }).ToList();
    }

    /// <summary>保持当前选中项,把设备列表红/绿点同步为当前连接状态(连接建立/失败/设备丢失后调用)。</summary>
    private void SyncChannelState()
    {
        if (DevicesList.ItemsSource is not IEnumerable<DeviceInfo> current) return;
        var selectedId = (DevicesList.SelectedItem as DeviceInfo)?.Id;
        var updated = MapChannelState(current);
        DevicesList.ItemsSource = updated;
        if (selectedId != null)
        {
            DevicesList.SelectedItem = updated.FirstOrDefault(d => d.Id == selectedId);
        }
    }

    private async Task ConnectToDeviceAsync(DeviceInfo device, bool quiet)
    {
        try
        {
            if (!quiet) StatusText.Text = $"正在启动设备端 bftpd 并建立 FTP 通道({device.Id})…";
            _ftp = new FtpFileService(_hdc, _configuration.FtpAccess, _configuration.ManagedApplication, _pathPolicy, _auditService, device.Id);
            await _ftp.ConnectAsync(CancellationToken.None);
            SetFtpState(connected: true);
            SyncChannelState(); // 绿点:该设备通道已就绪
            await LoadFilesAsync();
        }
        catch (Exception exception)
        {
            _ftp = null;
            SetFtpState(connected: false);
            SyncChannelState(); // 红点:通道未建立
            StatusText.Text = quiet ? $"自动连接失败：{exception.Message}" : $"连接失败：{exception.Message}";
            await _auditService.WriteAsync("ftp-connect-failed", exception.Message);
        }
    }

    private async void RootSelector_SelectionChanged(object sender, System.Windows.Controls.SelectionChangedEventArgs e)
    {
        _currentPath = string.Empty;
        await LoadFilesAsync();
    }

    private async void RefreshFiles_Click(object sender, RoutedEventArgs e) => await LoadFilesAsync();

    private async void GoUp_Click(object sender, RoutedEventArgs e)
    {
        if (string.IsNullOrEmpty(_currentPath))
        {
            StatusText.Text = "已在当前维护根目录。";
            return;
        }
        var parts = _currentPath.Split('/', StringSplitOptions.RemoveEmptyEntries);
        _currentPath = parts.Length <= 1 ? string.Empty : string.Join("/", parts.Take(parts.Length - 1));
        await LoadFilesAsync();
    }

    /// <summary>跳转到面包屑指定的沙箱路径(须在当前维护根目录下且通过敏感路径校验)。</summary>
    private async Task NavigateToPathAsync(string remotePath)
    {
        if (_ftp == null) { StatusText.Text = "请先连接设备。"; return; }
        if (string.IsNullOrWhiteSpace(remotePath)) return;

        var sandboxRoot = CurrentSandboxRoot.TrimEnd('/');
        var normalized = remotePath.Replace('\\', '/').Trim().TrimEnd('/');
        if (!normalized.Equals(sandboxRoot, StringComparison.OrdinalIgnoreCase)
            && !normalized.StartsWith(sandboxRoot + "/", StringComparison.OrdinalIgnoreCase))
        {
            StatusText.Text = "仅支持当前维护根目录下的路径。";
            return;
        }
        try { _pathPolicy.EnsureAllowed(_configuration.ManagedApplication, normalized); }
        catch (Exception exception) { StatusText.Text = exception.Message; return; }

        _currentPath = normalized.Length > sandboxRoot.Length ? normalized[(sandboxRoot.Length + 1)..] : string.Empty;
        await LoadFilesAsync();
    }

    /// <summary>把当前路径拆成可点击的段落,点击任意一段直接进入该文件夹。</summary>
    private void BuildBreadcrumb(string remoteDir)
    {
        BreadcrumbPanel.Children.Clear();
        var parts = remoteDir.Split('/', StringSplitOptions.RemoveEmptyEntries);
        var cumulative = string.Empty;
        for (var i = 0; i < parts.Length; i++)
        {
            cumulative += "/" + parts[i];
            var isCurrent = i == parts.Length - 1;
            var path = cumulative;
            var button = new Button
            {
                Content = parts[i],
                Tag = path,
                ToolTip = path,
                Padding = new Thickness(4, 1, 4, 1),
                BorderThickness = new Thickness(0),
                Background = Brushes.Transparent,
                Cursor = Cursors.Hand,
                FontSize = 14,
                Foreground = isCurrent
                    ? new SolidColorBrush(Color.FromRgb(0x1E, 0x29, 0x3B))
                    : new SolidColorBrush(Color.FromRgb(0x25, 0x6F, 0xEB)),
                FontWeight = isCurrent ? FontWeights.SemiBold : FontWeights.Normal
            };
            if (!isCurrent) button.Click += (_, _) => _ = NavigateToPathAsync(path);
            BreadcrumbPanel.Children.Add(button);

            if (i < parts.Length - 1)
            {
                BreadcrumbPanel.Children.Add(new TextBlock
                {
                    Text = "›",
                    VerticalAlignment = VerticalAlignment.Center,
                    Margin = new Thickness(1, 0, 1, 0),
                    Foreground = new SolidColorBrush(Color.FromRgb(0x94, 0xA3, 0xB8))
                });
            }
        }
    }

    private async void FilesGrid_MouseDoubleClick(object sender, System.Windows.Input.MouseButtonEventArgs e)
    {
        if (FilesGrid.SelectedItem is not MaintenanceFileItem { Type: "directory" } item) return;
        _currentPath = item.Path[(CurrentSandboxRoot.Length + 1)..];
        await LoadFilesAsync();
    }

    private void FilesGrid_PreviewMouseLeftButtonDown(object sender, System.Windows.Input.MouseButtonEventArgs e)
    {
        _dragStartPoint = e.GetPosition(FilesGrid);
        _draggingStarted = false;
        // 快照按下瞬间的选中集:DataGrid 在按住拖动时可能改写 SelectedItems,
        // 提前记录避免拖出时漏掉多选。点击已选中行则保留整个多选,否则视为单选该行。
        var row = ItemsControl.ContainerFromElement(FilesGrid, e.OriginalSource as DependencyObject) as DataGridRow;
        var clickedItem = row?.Item as MaintenanceFileItem;
        var current = FilesGrid.SelectedItems.OfType<MaintenanceFileItem>().ToList();
        _dragItems = clickedItem != null && current.Contains(clickedItem)
            ? current
            : (clickedItem != null ? new List<MaintenanceFileItem> { clickedItem } : new List<MaintenanceFileItem>());
    }

    private void FilesGrid_PreviewMouseLeftButtonUp(object sender, System.Windows.Input.MouseButtonEventArgs e) => _draggingStarted = false;

    private async void FilesGrid_MouseMove(object sender, System.Windows.Input.MouseEventArgs e)
    {
        if (e.LeftButton != System.Windows.Input.MouseButtonState.Pressed || _draggingStarted || _ftp == null) return;
        var position = e.GetPosition(FilesGrid);
        if (Math.Abs(position.X - _dragStartPoint.X) < SystemParameters.MinimumHorizontalDragDistance
            && Math.Abs(position.Y - _dragStartPoint.Y) < SystemParameters.MinimumVerticalDragDistance) return;

        var items = _dragItems.Where(item => item.Type == "file").ToList();
        if (items.Count == 0) return;
        _draggingStarted = true;
        await PrepareDragFilesAsync(items);
    }

    /// <summary>把选中的设备文件先下载到本地临时缓存,再以 FileDrop 格式拖出到桌面/资源管理器。</summary>
    private async Task PrepareDragFilesAsync(IReadOnlyList<MaintenanceFileItem> items)
    {
        _isDragging = true;
        try
        {
            SetSelectedRowsOpacity(0.45); // 拖起时的选中状态反馈
            var saveFolder = _dragSaveFolder;
            StatusText.Text = $"正在下载 {items.Count} 个文件到 {saveFolder}…";
            var success = 0;
            using var semaphore = new SemaphoreSlim(4);
            await Task.WhenAll(items.Select(async item =>
            {
                await semaphore.WaitAsync();
                try
                {
                    var localPath = Path.Combine(saveFolder, SanitizeFileName(item.Name));
                    await _ftp!.DownloadAsync(item.Path, localPath, CancellationToken.None);
                    Interlocked.Increment(ref success);
                }
                finally { semaphore.Release(); }
            }));
            await _auditService.WriteAsync("file-drag-out", string.Join(";", items.Select(item => item.Path)));
            StatusText.Text = $"已下载 {success}/{items.Count} 个文件到 {saveFolder}。";
        }
        catch (Exception exception)
        {
            StatusText.Text = $"下载失败：{exception.Message}";
        }
        finally
        {
            SetSelectedRowsOpacity(1.0);
            _isDragging = false;
            _draggingStarted = false;
        }
    }

    /// <summary>把源列表中正在拖拽的行整体调低透明度,作为“已拿起”的选中反馈。</summary>
    private void SetSelectedRowsOpacity(double opacity)
    {
        foreach (var item in FilesGrid.SelectedItems)
        {
            if (FilesGrid.ItemContainerGenerator.ContainerFromItem(item) is DataGridRow row)
            {
                row.Opacity = opacity;
            }
        }
    }

    private static string SanitizeFileName(string name)
    {
        foreach (var invalid in Path.GetInvalidFileNameChars()) name = name.Replace(invalid, '_');
        return name;
    }

    private void FilesGrid_DragOver(object sender, DragEventArgs e)
    {
        e.Effects = e.Data.GetDataPresent(DataFormats.FileDrop) ? DragDropEffects.Copy : DragDropEffects.None;
        e.Handled = true;
    }

    private async void FilesGrid_Drop(object sender, DragEventArgs e)
    {
        e.Handled = true;
        if (_ftp == null)
        {
            StatusText.Text = "请先连接设备。";
            return;
        }
        if (!e.Data.GetDataPresent(DataFormats.FileDrop)) return;
        var files = ((string[])e.Data.GetData(DataFormats.FileDrop)!).ToList();
        if (files.Count == 0) return;

        if (files.Any(Directory.Exists))
        {
            StatusText.Text = "正在上传文件与文件夹…";
        }

        var remoteDir = CurrentRemoteDir;
        var names = files.Select(Path.GetFileName).ToList();
        if (MessageBox.Show(this, $"将以下 {files.Count} 项(文件/文件夹)上传到\n{remoteDir}\n\n{string.Join("\n", names)}\n\n同名内容将被覆盖，是否继续？",
                "确认上传", MessageBoxButton.OKCancel, MessageBoxImage.Warning) != MessageBoxResult.OK) return;

        try
        {
            StatusText.Text = $"正在上传 {files.Count} 项…";
            foreach (var file in files)
            {
                if (Directory.Exists(file))
                {
                    await _ftp.UploadFolderAsync(file, $"{remoteDir}/{Path.GetFileName(file)}", CancellationToken.None);
                }
                else
                {
                    await _ftp.UploadAsync(file, $"{remoteDir}/{Path.GetFileName(file)}", CancellationToken.None);
                }
            }
            await _auditService.WriteAsync("file-drag-in", string.Join(";", files));
            StatusText.Text = $"已上传 {files.Count} 项。";
        }
        catch (Exception exception) { StatusText.Text = $"上传失败：{exception.Message}"; await _auditService.WriteAsync("upload-failed", exception.Message); }
        finally
        {
            await LoadFilesAsync(); // 无论成功失败,刷新界面反映实际状态
        }
    }

    private async void Download_Click(object sender, RoutedEventArgs e)
    {
        if (_ftp == null)
        {
            StatusText.Text = "请先连接设备。";
            return;
        }
        var selected = FilesGrid.SelectedItems.OfType<MaintenanceFileItem>().ToList();
        if (selected.Count == 0)
        {
            StatusText.Text = "请先选择要下载的文件或文件夹（可 Ctrl/Shift 多选或 Ctrl+A 全选）。";
            return;
        }

        var dialog = new Microsoft.Win32.OpenFolderDialog { Title = $"选择保存目录（{selected.Count} 项）" };
        if (dialog.ShowDialog(this) != true) return;
        var folder = dialog.FolderName;

        var success = 0;
        var failures = new List<string>();
        try
        {
            for (var i = 0; i < selected.Count; i++)
            {
                var item = selected[i];
                StatusText.Text = $"正在下载 {i + 1}/{selected.Count}: {item.Name}…";
                try
                {
                    var localPath = Path.Combine(folder, SanitizeFileName(item.Name));
                    if (item.Type == "directory")
                    {
                        await _ftp.DownloadFolderAsync(item.Path, localPath, CancellationToken.None);
                    }
                    else
                    {
                        await _ftp.DownloadAsync(item.Path, localPath, CancellationToken.None);
                    }
                    success++;
                }
                catch (Exception exception)
                {
                    failures.Add($"{item.Name}: {exception.Message}");
                }
            }
            StatusText.Text = failures.Count == 0
                ? $"已下载 {success}/{selected.Count} 项到 {folder}。"
                : $"已下载 {success}/{selected.Count} 项；失败 {failures.Count} 项：{string.Join("；", failures.Take(2))}…";
            await _auditService.WriteAsync("download-batch", $"{success}/{selected.Count} 成功; {string.Join(";", failures.Take(3))}");
        }
        catch (Exception exception) { StatusText.Text = $"下载失败：{exception.Message}（已完成 {success}/{selected.Count}）。"; }
    }

    private async void Upload_Click(object sender, RoutedEventArgs e)
    {
        if (_ftp == null)
        {
            StatusText.Text = "请先连接设备。";
            return;
        }

        var dialog = new Microsoft.Win32.OpenFileDialog { Filter = "所有文件|*.*" };
        if (dialog.ShowDialog(this) != true) return;

        var fileName = Path.GetFileName(dialog.FileName);
        var remotePath = $"{CurrentRemoteDir}/{fileName}";
        if (MessageBox.Show(this, $"将“{fileName}”上传到\n{remotePath}\n\n若同名文件已存在将被覆盖，是否继续？",
                "确认上传", MessageBoxButton.OKCancel, MessageBoxImage.Warning) != MessageBoxResult.OK) return;

        try
        {
            StatusText.Text = $"正在上传 {fileName} …";
            await _ftp.UploadAsync(dialog.FileName, remotePath, CancellationToken.None);
            StatusText.Text = $"已上传 {fileName}。";
        }
        catch (Exception exception) { StatusText.Text = $"上传失败：{exception.Message}"; }
        finally
        {
            await LoadFilesAsync(); // 无论成功失败,刷新界面反映实际状态
        }
    }

    private void Rename_Click(object sender, RoutedEventArgs e)
    {
        // 设备端 bftpd 对匿名用户禁用 RNTO(451),ftpput -m 同样受限,重命名无法通过受控通道实现
        StatusText.Text = "设备端 bftpd 不允许重命名(权限限制),当前版本暂不支持。";
    }

    private async void Delete_Click(object sender, RoutedEventArgs e) => await DeleteSelectedAsync();

    private async void FilesGrid_PreviewKeyDown(object sender, System.Windows.Input.KeyEventArgs e)
    {
        if (e.Key == System.Windows.Input.Key.Delete)
        {
            e.Handled = true;
            await DeleteSelectedAsync();
        }
        else if (e.Key == System.Windows.Input.Key.A && Keyboard.Modifiers.HasFlag(ModifierKeys.Control))
        {
            e.Handled = true;
            FilesGrid.SelectAll();
        }
    }

    private async Task DeleteSelectedAsync()
    {
        if (_ftp == null)
        {
            StatusText.Text = "请先连接设备。";
            return;
        }

        var selected = FilesGrid.SelectedItems.OfType<MaintenanceFileItem>().ToList();
        if (selected.Count == 0)
        {
            StatusText.Text = "请先选择要删除的文件或目录。";
            return;
        }

        if (MessageBox.Show(this, $"确认删除 {selected.Count} 项？\n删除后不可恢复。", "删除确认",
                MessageBoxButton.OKCancel, MessageBoxImage.Warning) != MessageBoxResult.OK) return;

        try
        {
            await _ftp.DeleteAsync(selected.Select(item => item.Path).ToList(), CancellationToken.None);
            StatusText.Text = $"已删除 {selected.Count} 项。";
        }
        catch (Exception exception) { StatusText.Text = $"删除失败：{exception.Message}"; }
        finally
        {
            await LoadFilesAsync(); // 无论成功失败,刷新界面反映实际状态
        }
    }

    // ---------- 安装包投放区(hdc install 官方通道,与沙箱 FTP 文件维护相互独立) ----------

    private static readonly string[] InstallPackageExtensions = { ".hap", ".app" };

    private static bool IsInstallPackage(string path)
        => InstallPackageExtensions.Contains(Path.GetExtension(path).ToLowerInvariant());

    private void InstallDropZone_DragOver(object sender, DragEventArgs e)
    {
        e.Effects = HasInstallPackageFile(e.Data) ? DragDropEffects.Copy : DragDropEffects.None;
        e.Handled = true;
    }

    private static bool HasInstallPackageFile(IDataObject data)
        => data.GetDataPresent(DataFormats.FileDrop)
           && ((string[])data.GetData(DataFormats.FileDrop)!).Any(IsInstallPackage);

    private async void InstallDropZone_Drop(object sender, DragEventArgs e)
    {
        e.Handled = true;
        if (!e.Data.GetDataPresent(DataFormats.FileDrop)) return;
        var packages = ((string[])e.Data.GetData(DataFormats.FileDrop)!)
            .Where(IsInstallPackage).ToList();
        if (packages.Count == 0)
        {
            StatusText.Text = "请拖入 .hap 安装包，其他文件类型不支持安装。";
            return;
        }
        await InstallPackagesAsync(packages);
    }

    private async void InstallDropZone_Click(object sender, MouseButtonEventArgs e)
    {
        if (_installingPackage)
        {
            StatusText.Text = "已有安装任务进行中，请稍候…";
            return;
        }
        var dialog = new Microsoft.Win32.OpenFileDialog
        {
            Title = "选择要安装到设备的鸿蒙安装包",
            Filter = "鸿蒙安装包|*.hap|所有文件|*.*",
            Multiselect = true
        };
        if (dialog.ShowDialog(this) != true) return;
        await InstallPackagesAsync(dialog.FileNames);
    }

    /// <summary>把选中的安装包经 hdc 官方通道安装到当前所选设备;安装前二次确认并全程审计。
    /// 安装命令无回显,期间用进度条+心跳文本反馈仍在正常执行,避免看起来像卡死。</summary>
    private async Task InstallPackagesAsync(IReadOnlyList<string> packagePaths)
    {
        if (_installingPackage)
        {
            StatusText.Text = "已有安装任务进行中，请稍候…";
            return;
        }
        if (!_hdc.IsAvailable)
        {
            StatusText.Text = "未找到 hdc.exe，请先点击「HDC 设置」完成工具链配置。";
            return;
        }
        var device = ResolveInstallTargetDevice();
        if (device == null)
        {
            StatusText.Text = "未发现可用设备，请先在左侧选择或连接设备。";
            return;
        }

        var details = string.Join("\n", packagePaths.Select(p => $"{Path.GetFileName(p)}（{FormatSize(new FileInfo(p).Length)}）"));
        var message = $"将把以下安装包安装到设备\n{device.DisplayName}\n\n{details}\n\n若设备已安装同名应用，将执行覆盖更新。是否继续？";
        if (MessageBox.Show(this, message, "确认安装", MessageBoxButton.OKCancel, MessageBoxImage.Warning) != MessageBoxResult.OK)
        {
            return;
        }

        _installingPackage = true;
        InstallStatusText.Visibility = Visibility.Visible;
        InstallProgress.Visibility = Visibility.Visible;
        try
        {
            var success = 0;
            var failures = new List<string>();
            for (var i = 0; i < packagePaths.Count; i++)
            {
                var path = packagePaths[i];
                var fileName = Path.GetFileName(path);
                var size = FormatSize(new FileInfo(path).Length);
                StatusText.Text = $"正在安装 {fileName}（{i + 1}/{packagePaths.Count}）到 {device.DisplayName}…";
                try
                {
                    var (output, elapsedSeconds) = await InstallWithFeedbackAsync(device.Id, path, fileName, size);
                    await _auditService.WriteAsync("package-install", $"{fileName} -> {device.Id} {SummarizeOutput(output)}");
                    InstallStatusText.Text = $"✔ 已安装 {fileName}（{size}），用时 {elapsedSeconds:0} 秒。";
                    success++;
                }
                catch (Exception exception)
                {
                    failures.Add($"{fileName}: {exception.Message}");
                    InstallStatusText.Text = $"✘ {fileName} 安装失败：{exception.Message}";
                    await _auditService.WriteAsync("package-install-failed", $"{fileName} -> {device.Id} {exception.Message}");
                }
            }
            StatusText.Text = failures.Count == 0
                ? $"安装完成：{success}/{packagePaths.Count} 个安装包已安装到 {device.DisplayName}。"
                : $"安装完成 {success}/{packagePaths.Count}，失败 {failures.Count} 项：{string.Join("；", failures.Take(2))}…";
        }
        finally
        {
            _installingPackage = false;
            InstallProgress.Visibility = Visibility.Collapsed;
        }
    }

    /// <summary>带心跳的 hdc install:命令本身无回显,期间每 1 秒刷新一次状态文本提示已等待时长,
    /// 让用户判断仍在正常执行。返回设备输出与总耗时。</summary>
    private async Task<(string Output, double ElapsedSeconds)> InstallWithFeedbackAsync(
        string deviceId, string packagePath, string fileName, string size)
    {
        var stopwatch = System.Diagnostics.Stopwatch.StartNew();
        var task = _hdc.InstallPackageAsync(deviceId, packagePath, overwrite: true, CancellationToken.None);
        while (!task.IsCompleted)
        {
            await Task.WhenAny(task, Task.Delay(1000));
            if (!task.IsCompleted)
            {
                var seconds = (int)stopwatch.Elapsed.TotalSeconds;
                var hint = seconds <= 10
                    ? $"正在向设备推送并安装 {fileName}（{size}）…"
                    : $"正在安装 {fileName}（{size}）… 已等待 {seconds} 秒，请保持设备连接（较大安装包可能需要数分钟）";
                StatusText.Text = $"正在安装 {fileName}… 已等待 {seconds} 秒";
                InstallStatusText.Text = hint;
            }
        }
        var output = await task;
        stopwatch.Stop();
        return (output, stopwatch.Elapsed.TotalSeconds);
    }

    private static string FormatSize(long bytes)
    {
        var units = new[] { "B", "KB", "MB", "GB" };
        var value = (double)bytes;
        var index = 0;
        while (value >= 1024 && index < units.Length - 1)
        {
            value /= 1024;
            index++;
        }
        return $"{value:0.#} {units[index]}";
    }

    /// <summary>安装目标设备:优先左侧列表当前选中项,否则取列表第一台。</summary>
    private DeviceInfo? ResolveInstallTargetDevice()
    {
        if (DevicesList.SelectedItem is DeviceInfo selected) return selected;
        return (DevicesList.ItemsSource as IEnumerable<DeviceInfo>)?.FirstOrDefault();
    }

    private static string SummarizeOutput(string? output)
    {
        var text = (output ?? string.Empty).Trim();
        return text.Length <= 200 ? text : text[..200];
    }

    private async Task LoadFilesAsync(bool quiet = false)
    {
        if (_ftp == null)
        {
            if (!quiet) StatusText.Text = "请先选择设备并连接。";
            return;
        }
        if (RootSelector.SelectedItem is not string root) return;

        // 用户操作(非 quiet)取消上一次尚未完成的加载(含自动刷新),保证立即生效;
        // 自动刷新(quiet)不打断用户操作,排队等待即可。不 Dispose CTS,避免运行中的旧操作抛 ObjectDisposedException。
        if (!quiet) _loadCts?.Cancel();
        _loadCts = new CancellationTokenSource();
        var loadToken = _loadCts.Token;

        try
        {
            var remoteDir = CurrentRemoteDir;
            if (!quiet)
            {
                BuildBreadcrumb(remoteDir);
                StatusText.Text = "正在读取受控沙箱目录…";
            }
            var items = await _ftp.ListFilesAsync(remoteDir, loadToken);
            if (loadToken.IsCancellationRequested) return; // 已被更新的操作取代
            if (quiet && SameListing(_lastFiles, items))
            {
                return; // 内容无变化,保持选中与滚动位置
            }
            // 记录当前选中路径,刷新后恢复,避免自动刷新打断多选
            var selectedPaths = FilesGrid.SelectedItems.OfType<MaintenanceFileItem>().Select(item => item.Path).ToHashSet();
            _lastFiles = items;
            FilesGrid.ItemsSource = items;
            foreach (var item in items)
            {
                if (selectedPaths.Contains(item.Path))
                {
                    FilesGrid.SelectedItems.Add(item);
                }
            }
            if (!quiet)
            {
                await _auditService.WriteAsync("file-list", remoteDir);
                StatusText.Text = $"已读取 {items.Count} 项（每 5 秒自动刷新）。";
            }
        }
        catch (OperationCanceledException)
        {
            // 被更新的操作或刷新取代,静默返回
        }
        catch (Exception exception)
        {
            StatusText.Text = $"目录读取失败：{exception.Message}";
            await _auditService.WriteAsync("list-failed", exception.ToString());
        }
    }

    private async void AutoRefreshTick(object? sender, EventArgs e)
    {
        if (_refreshing || _isDragging) return;
        _refreshing = true;
        try
        {
            // 自动刷新只做轻量设备在线检查,不做 FTP 列表:
            // 自动列表会与用户操作竞争 FTP 锁并可能因设备端命令偶发变慢而阻塞操作,是卡顿的根源。
            if (_ftp?.IsConnected == true)
            {
                // 已连接:确认当前设备仍在 hdc 设备列表中,否则设备已拔出/断开,复位连接状态,
                // 避免界面仍显示“已连接”但操作全部失败。
                var devices = await _hdc.ListDevicesAsync(CancellationToken.None);
                if (devices.All(d => d.Id != _ftp.DeviceId))
                {
                    var lostDevice = _ftp.DeviceId;
                    _ftp = null;
                    SetFtpState(connected: false);
                    DevicesList.ItemsSource = MapChannelState(devices); // 红点:通道已失效
                    StatusText.Text = "设备已断开,请重新连接。";
                    await _auditService.WriteAsync("device-lost", lostDevice);
                }
                return;
            }
            await RefreshDevicesAsync(quiet: true);
        }
        finally { _refreshing = false; }
    }

    private static bool SameListing(IReadOnlyList<MaintenanceFileItem>? previous, IReadOnlyList<MaintenanceFileItem> current)
    {
        if (previous == null || previous.Count != current.Count) return false;
        for (var i = 0; i < previous.Count; i++)
        {
            if (previous[i].Name != current[i].Name
                || previous[i].Type != current[i].Type
                || previous[i].Size != current[i].Size
                || previous[i].ModifiedAt != current[i].ModifiedAt)
            {
                return false;
            }
        }
        return true;
    }

    private string CurrentRootKey => RootSelector.SelectedItem as string ?? throw new InvalidOperationException("未选择维护根目录。");

    private string CurrentSandboxRoot => _configuration.ManagedApplication.AllowedRoots[CurrentRootKey];

    private string CurrentRemoteDir => string.IsNullOrEmpty(_currentPath)
        ? CurrentSandboxRoot
        : $"{CurrentSandboxRoot}/{_currentPath}";

    private void SetFtpState(bool connected)
    {
        RootSelector.IsEnabled = connected;
        RefreshButton.IsEnabled = connected;
        FilesGrid.IsEnabled = connected;
        DownloadButton.IsEnabled = connected;
        UploadButton.IsEnabled = connected;
        RenameButton.IsEnabled = connected;
        DeleteButton.IsEnabled = connected;
    }

    private static ToolConfiguration LoadConfiguration()
    {
        var path = Path.Combine(AppContext.BaseDirectory, "appsettings.json");
        var json = File.ReadAllText(path);
        return JsonSerializer.Deserialize<ToolConfiguration>(json, new JsonSerializerOptions { PropertyNameCaseInsensitive = true })
               ?? throw new InvalidOperationException("appsettings.json 格式无效。");
    }
}

public sealed class ToolConfiguration
{
    public required string HdcPath { get; init; }
    public required FtpAccessOptions FtpAccess { get; init; }
    public required ManagedAppPolicy ManagedApplication { get; init; }
    public MediaAccessOptions? MediaAccess { get; init; }
    public HdcInstallOptions? HdcInstall { get; init; }
}
