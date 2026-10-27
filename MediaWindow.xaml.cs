using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using HarmonyFileManager.Models;
using HarmonyFileManager.Services;

namespace HarmonyFileManager;

/// <summary>独立的相册/视频下载窗口。与主窗口的文件管理逻辑完全独立,通过 mediatool 访问系统媒体库。</summary>
public partial class MediaWindow : Window
{
    private readonly MediaAccessOptions _config;
    private readonly HdcService _hdc;
    private readonly AuditService _audit;
    private MediaService? _media;
    private string _currentType = string.Empty;
    private string _currentBucket = string.Empty;
    private bool _refreshing;
    private bool _downloading;
    private readonly TransferProgressTracker _progressTracker = new();
    private string _downloadFolder = string.Empty;

    public MediaWindow(HdcService hdc, AuditService audit, MediaAccessOptions config)
    {
        InitializeComponent();
        _hdc = hdc;
        _audit = audit;
        _config = config;
        TypeSelector.ItemsSource = _config.MediaTypes.Keys.ToList();
        TypeSelector.SelectedIndex = 0;
        StatusText.Text = "请先选择设备；每 5 秒自动检测设备。";
        _downloadFolder = _config.ResolveDownloadFolder();
        DownloadFolderText.Text = _downloadFolder;
        DownloadFolderText.ToolTip = _downloadFolder;

        var timer = new System.Windows.Threading.DispatcherTimer { Interval = TimeSpan.FromSeconds(5) };
        timer.Tick += async (_, _) => await AutoRefreshAsync();
        timer.Start();
    }

    // ---------- 设备 ----------

    private async void RefreshDevices_Click(object sender, RoutedEventArgs e) => await RefreshDevicesAsync(quiet: false);

    private async Task RefreshDevicesAsync(bool quiet)
    {
        if (_downloading) return;
        if (!_hdc.IsAvailable)
        {
            if (!quiet) StatusText.Text = "未找到 hdc.exe，请先在主窗口完成「HDC 设置」。";
            return;
        }
        try
        {
            if (!quiet) StatusText.Text = "正在检测 HDC 设备…";
            var devices = await _hdc.ListDevicesAsync(CancellationToken.None);
            DevicesList.ItemsSource = devices;
            if (devices.Count > 0 && DevicesList.SelectedIndex < 0) DevicesList.SelectedIndex = 0;

            if (devices.Count > 0 && (_media == null || !devices.Any(d => d.Id == _media.DeviceId)))
            {
                await ConnectDeviceAsync(devices[0]);
            }

            if (!quiet)
            {
                await _audit.WriteAsync("media-device-list", $"发现 {devices.Count} 台设备");
                StatusText.Text = devices.Count == 0 ? "未发现设备。" : $"已发现 {devices.Count} 台设备。";
            }
        }
        catch (Exception exception)
        {
            StatusText.Text = $"设备检测失败：{exception.Message}";
        }
    }

    private async Task ConnectDeviceAsync(DeviceInfo device)
    {
        _media = new MediaService(_hdc, _audit, device.Id, _config.MediaRoot);
        await _audit.WriteAsync("media-device-connect", device.Id);
        StatusText.Text = $"已连接设备 {device.Id}。";
        if (TypeSelector.SelectedItem is string type)
        {
            _currentType = _config.MediaTypes[type];
            await LoadBucketsAsync();
        }
    }

    // ---------- 媒体类型 / 分桶 ----------

    private async void TypeSelector_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (TypeSelector.SelectedItem is not string type) return;
        _currentType = _config.MediaTypes[type];
        _currentBucket = string.Empty;
        BucketSelector.ItemsSource = null;
        FilesGrid.ItemsSource = null;
        BuildBreadcrumb(string.Empty);
        if (_media != null) await LoadBucketsAsync();
    }

    private async void BucketSelector_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (BucketSelector.SelectedItem is not string bucket) return;
        _currentBucket = bucket;
        if (_media != null) await LoadFilesAsync();
    }

    private async void RefreshFiles_Click(object sender, RoutedEventArgs e)
    {
        if (_media == null) { StatusText.Text = "请先连接设备。"; return; }
        if (string.IsNullOrEmpty(_currentBucket)) await LoadBucketsAsync();
        else await LoadFilesAsync();
    }

    // ---------- 列表加载 ----------

    private async Task LoadBucketsAsync()
    {
        if (_media == null) return;
        try
        {
            StatusText.Text = "正在读取分桶列表…";
            var buckets = await _media.ListBucketsAsync(_currentType, CancellationToken.None);
            BucketSelector.ItemsSource = buckets.Select(b => b.Name).ToList();
            FilesGrid.ItemsSource = buckets;
            BuildBreadcrumb(_currentType);
            BackButton.Visibility = Visibility.Collapsed;
            StatusText.Text = $"共 {buckets.Count} 个分桶，双击或在上方选择分桶进入。";
        }
        catch (Exception exception) { StatusText.Text = $"读取分桶失败：{exception.Message}"; }
    }

    private async Task LoadFilesAsync()
    {
        if (_media == null || string.IsNullOrEmpty(_currentBucket)) return;
        try
        {
            StatusText.Text = "正在读取媒体文件…";
            var files = await _media.ListFilesAsync(_currentType, _currentBucket, CancellationToken.None);
            FilesGrid.ItemsSource = files;
            BuildBreadcrumb($"{_currentType}/{_currentBucket}");
            BackButton.Visibility = Visibility.Visible;
            StatusText.Text = $"共 {files.Count} 项，可多选后下载（Ctrl/Shift 多选，Ctrl+A 全选）。";
        }
        catch (Exception exception) { StatusText.Text = $"读取文件失败：{exception.Message}"; }
    }

    // ---------- 交互 ----------

    private void FilesGrid_MouseDoubleClick(object sender, MouseButtonEventArgs e)
    {
        if (FilesGrid.SelectedItem is MediaItem { Type: "directory" } item
            && BucketSelector.ItemsSource is System.Collections.IEnumerable buckets
            && buckets.Cast<string>().Contains(item.Name))
        {
            BucketSelector.SelectedItem = item.Name;
        }
    }

    private void FilesGrid_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.A && Keyboard.Modifiers.HasFlag(ModifierKeys.Control))
        {
            e.Handled = true;
            FilesGrid.SelectAll();
        }
    }

    private async void BackButton_Click(object sender, RoutedEventArgs e)
    {
        _currentBucket = string.Empty;
        BucketSelector.SelectedItem = null;
        await LoadBucketsAsync();
    }

    // ---------- 拖拽下载 ----------

    private Point _dragStartPoint;
    private bool _draggingStarted;
    private IReadOnlyList<MediaItem> _dragItems = [];

    private void FilesGrid_PreviewMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        _dragStartPoint = e.GetPosition(FilesGrid);
        _draggingStarted = false;
        // 快照按下瞬间的选中集:DataGrid 在按住拖动时可能改写 SelectedItems,提前记录避免拖出时漏掉多选。
        var row = ItemsControl.ContainerFromElement(FilesGrid, e.OriginalSource as DependencyObject) as DataGridRow;
        var clickedItem = row?.Item as MediaItem;
        var current = FilesGrid.SelectedItems.OfType<MediaItem>().ToList();
        _dragItems = clickedItem != null && current.Contains(clickedItem)
            ? current
            : (clickedItem != null ? new List<MediaItem> { clickedItem } : new List<MediaItem>());
    }

    private void FilesGrid_PreviewMouseLeftButtonUp(object sender, MouseButtonEventArgs e) => _draggingStarted = false;

    private async void FilesGrid_MouseMove(object sender, MouseEventArgs e)
    {
        if (_downloading || e.LeftButton != MouseButtonState.Pressed || _draggingStarted || _media == null) return;
        var position = e.GetPosition(FilesGrid);
        if (Math.Abs(position.X - _dragStartPoint.X) < SystemParameters.MinimumHorizontalDragDistance
            && Math.Abs(position.Y - _dragStartPoint.Y) < SystemParameters.MinimumVerticalDragDistance) return;

        var items = _dragItems.Where(item => item.Type == "file").ToList();
        if (items.Count == 0) return;
        _draggingStarted = true;

        var tempFolder = Path.Combine(Path.GetTempPath(), $"hfm_drag_{Guid.NewGuid():N}");
        try
        {
            StatusText.Text = $"正在准备拖拽下载 {items.Count} 个文件…";
            Directory.CreateDirectory(tempFolder);
            var service = _media!;
            var progress = BeginDownload(items);
            var localPaths = new List<string>();
            var success = 0;
            using var semaphore = new SemaphoreSlim(4);
            await Task.WhenAll(items.Select(async item =>
            {
                await semaphore.WaitAsync();
                try
                {
                    var localPath = Path.Combine(tempFolder, SanitizeFileName(item.Name));
                    await service.DownloadAsync(item.Path, localPath, CancellationToken.None, progress, item.Size);
                    lock (localPaths) localPaths.Add(localPath);
                    Interlocked.Increment(ref success);
                }
                finally { semaphore.Release(); }
            }));

            if (success == 0)
            {
                StatusText.Text = "拖拽下载失败，没有文件成功下载。";
                return;
            }

            StatusText.Text = $"已准备 {success}/{items.Count} 个文件，拖到目标位置（桌面/文件夹）释放即可。";
            var data = new DataObject(DataFormats.FileDrop, localPaths.ToArray());
            DragDrop.DoDragDrop(FilesGrid, data, DragDropEffects.Copy);
            await _audit.WriteAsync("media-drag-out", string.Join(";", localPaths));
            StatusText.Text = $"已拖拽 {success} 个文件。";
        }
        catch (Exception exception)
        {
            StatusText.Text = $"拖拽下载失败：{exception.Message}";
        }
        finally
        {
            EndDownload();
            _draggingStarted = false;
            try { Directory.Delete(tempFolder, true); } catch { /* 拖拽中文件被占用时忽略 */ }
        }
    }

    // ---------- 下载 ----------

    private async void Download_Click(object sender, RoutedEventArgs e)
    {
        if (_downloading) return;
        if (_media == null) { StatusText.Text = "请先连接设备。"; return; }
        var selected = FilesGrid.SelectedItems.OfType<MediaItem>().Where(i => i.Type == "file").ToList();
        if (selected.Count == 0)
        {
            StatusText.Text = "请先选择要下载的媒体文件（需先进入分桶，可 Ctrl/Shift 多选或 Ctrl+A 全选）。";
            return;
        }

        var folder = _downloadFolder;
        var existing = selected.Select(item => Path.Combine(folder, SanitizeFileName(item.Name)))
            .Where(File.Exists).ToList();
        if (existing.Count > 0 && MessageBox.Show(this,
            $"下载将覆盖 {existing.Count} 个本地同名文件，是否继续？", "确认覆盖",
            MessageBoxButton.OKCancel, MessageBoxImage.Warning) != MessageBoxResult.OK) return;
        var service = _media;
        var progress = BeginDownload(selected);

        var success = 0;
        var failures = new List<string>();
        try
        {
            Directory.CreateDirectory(folder);
            for (var i = 0; i < selected.Count; i++)
            {
                var item = selected[i];
                StatusText.Text = $"正在下载 {i + 1}/{selected.Count}: {item.Name}…";
                try
                {
                    var localPath = Path.Combine(folder, SanitizeFileName(item.Name));
                    await service.DownloadAsync(item.Path, localPath, CancellationToken.None, progress, item.Size);
                    success++;
                }
                catch (Exception exception) { failures.Add($"{item.Name}: {exception.Message}"); }
            }
            StatusText.Text = failures.Count == 0
                ? $"已下载 {success}/{selected.Count} 项到 {folder}。"
                : $"已下载 {success}/{selected.Count} 项；失败 {failures.Count} 项：{string.Join("；", failures.Take(2))}";
            await _audit.WriteAsync("media-download-batch", $"{success}/{selected.Count} 成功; {string.Join(";", failures.Take(3))}");
        }
        catch (Exception exception) { StatusText.Text = $"下载失败：{exception.Message}（已完成 {success}/{selected.Count}）。"; }
        finally { EndDownload(); }
    }

    private void ChangeDownloadFolder_Click(object sender, RoutedEventArgs e)
    {
        if (_downloading) return;
        var dialog = new Microsoft.Win32.OpenFolderDialog
        {
            Title = "选择下载目录",
            InitialDirectory = Directory.Exists(_downloadFolder) ? _downloadFolder : Environment.GetFolderPath(Environment.SpecialFolder.Desktop)
        };
        if (dialog.ShowDialog() == true)
        {
            try { Directory.CreateDirectory(dialog.FolderName); } catch { StatusText.Text = $"无法创建目录：{dialog.FolderName}"; return; }
            _downloadFolder = dialog.FolderName;
            DownloadFolderText.Text = _downloadFolder;
            DownloadFolderText.ToolTip = _downloadFolder;
            StatusText.Text = $"下载目录已更改为：{_downloadFolder}";
        }
    }

    private sealed class MediaProgress(Action<TransferProgress> report) : IProgress<TransferProgress>
    {
        public void Report(TransferProgress value) => report(value);
    }

    private IProgress<TransferProgress> BeginDownload(IReadOnlyList<MediaItem> items)
    {
        _downloading = true;
        DownloadButton.IsEnabled = false;
        TypeSelector.IsEnabled = BucketSelector.IsEnabled = DevicesList.IsEnabled = false;
        var total = items.Any(item => item.Size is null) ? -1 : checked(items.Sum(item => item.Size!.Value) * 2);
        _progressTracker.Reset(total);
        TransferPanel.Visibility = Visibility.Visible;
        DownloadProgress.Value = 0;
        DownloadProgress.IsIndeterminate = total < 0;
        ProgressInfoText.Text = "准备下载…";
        var done = new HashSet<string>();
        return new MediaProgress(value =>
        {
            if (!_downloading) return;
            if (value.State == "done") done.Add(value.TransferId);
            _progressTracker.Update(value);
            DownloadProgress.IsIndeterminate = total < 0 || value.TotalBytes < 0;
            DownloadProgress.Value = _progressTracker.Percent;
            var percent = DownloadProgress.IsIndeterminate ? "大小读取中" : $"{_progressTracker.Percent:0.#}%";
            ProgressInfoText.Text = $"完成 {done.Count}/{items.Count} · {value.FileName} · {value.Stage} · {percent}\n"
                + $"等效进度 {FormatBytes(_progressTracker.DoneActualBytes)} / {(total < 0 ? "未知" : FormatBytes(total / 2))}"
                + (DownloadProgress.IsIndeterminate ? "" : $" · {_progressTracker.SpeedText} · {_progressTracker.EtaText}");
        });
    }

    private void EndDownload()
    {
        _downloading = false;
        DownloadButton.IsEnabled = _media != null;
        TypeSelector.IsEnabled = BucketSelector.IsEnabled = DevicesList.IsEnabled = true;
        DownloadProgress.IsIndeterminate = false;
    }

    private static string FormatBytes(long bytes) => bytes >= 1024L * 1024 * 1024
        ? $"{bytes / (1024d * 1024 * 1024):0.##} GB"
        : bytes >= 1024 * 1024 ? $"{bytes / (1024d * 1024):0.##} MB"
        : bytes >= 1024 ? $"{bytes / 1024d:0.##} KB" : $"{bytes} B";

    // ---------- 面包屑 ----------

    private void BuildBreadcrumb(string remoteDir)
    {
        BreadcrumbPanel.Children.Clear();
        if (string.IsNullOrEmpty(remoteDir)) return;
        var parts = remoteDir.Split('/', StringSplitOptions.RemoveEmptyEntries);
        for (var i = 0; i < parts.Length; i++)
        {
            BreadcrumbPanel.Children.Add(new Button
            {
                Content = parts[i],
                Padding = new Thickness(4, 1, 4, 1),
                BorderThickness = new Thickness(0),
                Background = Brushes.Transparent,
                Cursor = Cursors.Hand,
                FontSize = 13,
                Foreground = i == parts.Length - 1
                    ? new SolidColorBrush(Color.FromRgb(0x1E, 0x29, 0x3B))
                    : new SolidColorBrush(Color.FromRgb(0x25, 0x6F, 0xEB)),
                FontWeight = i == parts.Length - 1 ? FontWeights.SemiBold : FontWeights.Normal
            });
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

    private static string SanitizeFileName(string name)
    {
        foreach (var invalid in Path.GetInvalidFileNameChars()) name = name.Replace(invalid, '_');
        return name;
    }

    // ---------- 自动刷新 ----------

    private async Task AutoRefreshAsync()
    {
        if (_downloading) return;
        if (_refreshing) return;
        _refreshing = true;
        try
        {
            if (_media != null)
            {
                var devices = await _hdc.ListDevicesAsync(CancellationToken.None);
                if (devices.All(d => d.Id != _media.DeviceId))
                {
                    var lost = _media.DeviceId;
                    _media = null;
                    DevicesList.ItemsSource = devices;
                    StatusText.Text = "设备已断开，请重新连接。";
                    await _audit.WriteAsync("media-device-lost", lost);
                }
                return;
            }
            await RefreshDevicesAsync(quiet: true);
        }
        finally { _refreshing = false; }
    }
}
