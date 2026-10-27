using HarmonyFileManager.Models;

namespace HarmonyFileManager.Services;

/// <summary>聚合逐文件进度事件,输出整体百分比/已传总大小/速度/剩余时间。
/// 以每次单文件传输的唯一 ID 汇总,同名文件及重试都独立计数。</summary>
public sealed class TransferProgressTracker
{
    private long _totalWork;          // 总工作量(2×实际字节)
    private readonly Dictionary<string, long> _fileWork = new();
    private readonly HashSet<string> _unfinished = new();
    private long _lastSampleWork;
    private long _lastSampleTicks;
    private double _speedWorkPerSec;

    public long TotalWork => _totalWork;
    public long DoneWork { get; private set; }
    public double Percent => _totalWork <= 0 ? 0 : Math.Min(_unfinished.Count == 0 ? 100 : 99.9, Math.Round(DoneWork * 100d / _totalWork, 1));
    public long DoneActualBytes => DoneWork / 2;
    public long TotalActualBytes => Math.Max(0, _totalWork) / 2;

    public void Reset(long totalWork)
    {
        _totalWork = totalWork;
        _fileWork.Clear();
        _unfinished.Clear();
        _lastSampleWork = 0;
        _lastSampleTicks = 0;
        _speedWorkPerSec = 0;
        DoneWork = 0;
    }

    public void Update(TransferProgress e)
    {
        var key = e.TransferId;
        _fileWork[key] = e.TotalBytes >= 0
            ? Math.Clamp(e.BytesTransferred, 0, e.TotalBytes)
            : Math.Max(0, e.BytesTransferred);
        if (e.State == "done") _unfinished.Remove(key);
        else _unfinished.Add(key);
        DoneWork = _fileWork.Values.Sum();

        var now = System.Diagnostics.Stopwatch.GetTimestamp();
        if (_lastSampleTicks != 0)
        {
            var dt = (now - _lastSampleTicks) / (double)System.Diagnostics.Stopwatch.Frequency;
            var delta = DoneWork - _lastSampleWork;
            if (dt >= 0.05 && delta >= 0)
            {
                var instant = delta / dt;
                _speedWorkPerSec = _speedWorkPerSec <= 0 ? instant : _speedWorkPerSec * 0.6 + instant * 0.4;
                _lastSampleWork = DoneWork;
                _lastSampleTicks = now;
            }
        }
        else
        {
            _lastSampleWork = DoneWork;
            _lastSampleTicks = now;
        }
    }

    public string SpeedText
    {
        get
        {
            var bytesPerSec = _speedWorkPerSec / 2;
            return bytesPerSec > 0 ? $"等效速度 {FormatRate(bytesPerSec)}/s" : "速度计算中";
        }
    }

    public string EtaText
    {
        get
        {
            var bytesPerSec = _speedWorkPerSec / 2;
            var remaining = TotalActualBytes - DoneActualBytes;
            if (bytesPerSec <= 0 || remaining <= 0) return string.Empty;
            return $"预计剩余 {FormatEta(remaining / bytesPerSec)}";
        }
    }

    private static string FormatRate(double bytesPerSec)
    {
        if (bytesPerSec >= 1024d * 1024 * 1024) return $"{bytesPerSec / (1024d * 1024 * 1024):0.#} GB";
        if (bytesPerSec >= 1024d * 1024) return $"{bytesPerSec / (1024d * 1024):0.#} MB";
        if (bytesPerSec >= 1024d) return $"{bytesPerSec / 1024d:0.#} KB";
        return $"{bytesPerSec:0} B";
    }

    private static string FormatEta(double seconds)
    {
        var total = TimeSpan.FromSeconds(seconds);
        if (total.TotalSeconds < 60) return $"{Math.Max(1, (int)Math.Ceiling(seconds))} 秒";
        if (total.TotalHours < 1) return $"{(int)total.TotalMinutes} 分 {total.Seconds} 秒";
        return $"{(int)total.TotalHours} 小时 {total.Minutes} 分";
    }
}

