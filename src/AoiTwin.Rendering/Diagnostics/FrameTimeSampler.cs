using System.Diagnostics;

namespace AoiTwin.Rendering.Diagnostics;

/// <summary>
/// 帧时间统计（NFR-01：3D 视口 60fps 基线）。窗口内滚动统计——只保留最近 N 帧，
/// 避免长时间运行后早期卡顿永久拉低均值，掩盖当前真实表现。
/// </summary>
public readonly record struct FrameTimeStats(
    double AverageFps,
    double AverageFrameMs,
    double P95FrameMs,
    double MaxFrameMs,
    long FrameCount)
{
    /// <summary>尚未采到样本时的空统计（面板显示「—」而非 0fps 误导）。</summary>
    public static FrameTimeStats Empty => new(0, 0, 0, 0, 0);

    public bool HasSamples => FrameCount > 0;
}

/// <summary>
/// 帧间时间采样器（T1.3）：纯计算，不依赖 WPF，可单测。
///
/// 判定口径与 PRD §7-4 一致：以「连续帧间隔」为样本——均值反映持续帧率，P95 反映掉帧，
/// 最大值用于发现加载/GC 造成的长卡顿（NFR-02：UI 线程不得出现 &gt; 100ms 阻塞）。
/// </summary>
public sealed class FrameTimeSampler
{
    private readonly double[] _intervals;
    private readonly int _windowSize;
    private int _next;
    private int _filled;

    public FrameTimeSampler(int windowSize = 120)
    {
        if (windowSize < 2)
        {
            throw new ArgumentOutOfRangeException(nameof(windowSize), "窗口至少 2 帧");
        }

        _windowSize = windowSize;
        _intervals = new double[windowSize];
    }

    public int WindowSize => _windowSize;

    /// <summary>累计记录的帧间隔数（不受窗口回绕影响）。</summary>
    public long FrameCount { get; private set; }

    /// <summary>记录一次帧间隔（毫秒）。非正数/非有限值忽略（首帧与时钟回拨）。</summary>
    public void RecordInterval(double milliseconds)
    {
        if (!double.IsFinite(milliseconds) || milliseconds <= 0)
        {
            return;
        }

        _intervals[_next] = milliseconds;
        _next = (_next + 1) % _windowSize;
        if (_filled < _windowSize)
        {
            _filled++;
        }

        FrameCount++;
    }

    /// <summary>用单调时钟打点：调用方负责记录上一帧时间戳（WPF 侧为 Stopwatch.GetTimestamp）。</summary>
    public void RecordTimestamp(long timestamp)
    {
        if (_lastTimestamp == 0)
        {
            _lastTimestamp = timestamp;
            return;
        }

        var delta = timestamp - _lastTimestamp;
        _lastTimestamp = timestamp;
        RecordInterval(delta * 1000.0 / Stopwatch.Frequency);
    }

    public void Reset()
    {
        Array.Clear(_intervals);
        _next = 0;
        _filled = 0;
        _lastTimestamp = 0;
        FrameCount = 0;
    }

    /// <summary>取当前窗口统计（无样本时返回 Empty）。</summary>
    public FrameTimeStats Sample()
    {
        if (_filled == 0)
        {
            return FrameTimeStats.Empty;
        }

        var samples = new double[_filled];
        Array.Copy(_intervals, samples, _filled);
        Array.Sort(samples);

        var sum = 0.0;
        foreach (var value in samples)
        {
            sum += value;
        }

        var average = sum / samples.Length;
        var p95Index = Math.Clamp((int)Math.Ceiling(samples.Length * 0.95) - 1, 0, samples.Length - 1);
        return new FrameTimeStats(
            AverageFps: average > 0 ? 1000.0 / average : 0,
            AverageFrameMs: average,
            P95FrameMs: samples[p95Index],
            MaxFrameMs: samples[^1],
            FrameCount: FrameCount);
    }

    private long _lastTimestamp;
}
