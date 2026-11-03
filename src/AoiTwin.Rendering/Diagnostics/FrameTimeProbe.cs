using System.Diagnostics;
using System.Windows.Media;

namespace AoiTwin.Rendering.Diagnostics;

/// <summary>
/// 渲染循环挂点 + 帧时间探针（T1.3 / NFR-01 / NFR-02）。
///
/// 挂点：<see cref="CompositionTarget.Rendering"/>——WPF 每提交一帧触发一次，与视口渲染同拍；
/// M2 的运动学插值、T1.8 的描边刷新都从这里取「每帧回调」，不另起计时器（避免与渲染节拍错相）。
/// 采样：帧间隔进 <see cref="FrameTimeSampler"/> 滚动窗口；统计每 250ms 对外播报一次，
/// 不是因为算不快，而是避免每帧都触发文本布局（探针本身成为掉帧源）。
/// </summary>
public sealed class FrameTimeProbe : IDisposable
{
    /// <summary>统计播报间隔：低于 4Hz 面板数字会跳，高于 4Hz 文本布局开销会干扰测量。</summary>
    private const double NotifyIntervalMs = 250;

    private readonly FrameTimeSampler _sampler;
    private readonly Stopwatch _clock = Stopwatch.StartNew();
    private bool _subscribed;
    private bool _disposed;

    public FrameTimeProbe(int windowSize = 120) => _sampler = new FrameTimeSampler(windowSize);

    /// <summary>每渲染帧回调（参数 = 距上一帧的间隔）。UI 线程；实现方不得阻塞。</summary>
    public event EventHandler<TimeSpan>? Frame;

    /// <summary>统计播报（约 4Hz）。UI 线程。</summary>
    public event EventHandler<FrameTimeStats>? StatsUpdated;

    public bool IsRunning => _subscribed;

    public FrameTimeStats Stats { get; private set; } = FrameTimeStats.Empty;

    public FrameTimeSampler Sampler => _sampler;

    /// <summary>开始采样（幂等）。只在 UI 线程调用。</summary>
    public void Start()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (_subscribed)
        {
            return;
        }

        // 首帧间隔无意义：重置窗口内的时间戳基线
        _sampler.Reset();
        _lastFrameTicks = 0;
        _lastNotifyTicks = _clock.Elapsed.TotalMilliseconds;
        CompositionTarget.Rendering += OnRendering;
        _subscribed = true;
    }

    public void Stop()
    {
        if (!_subscribed)
        {
            return;
        }

        CompositionTarget.Rendering -= OnRendering;
        _subscribed = false;
    }

    public void Reset() => _sampler.Reset();

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        Stop();
        _disposed = true;
    }

    private void OnRendering(object? sender, EventArgs e)
    {
        var now = Stopwatch.GetTimestamp();
        if (_lastFrameTicks == 0)
        {
            _lastFrameTicks = now;
            return;
        }

        var deltaTicks = now - _lastFrameTicks;
        _lastFrameTicks = now;
        var deltaMs = deltaTicks * 1000.0 / Stopwatch.Frequency;
        _sampler.RecordInterval(deltaMs);

        Frame?.Invoke(this, TimeSpan.FromTicks((long)(deltaMs * TimeSpan.TicksPerMillisecond)));

        var elapsed = _clock.Elapsed.TotalMilliseconds;
        if (elapsed - _lastNotifyTicks < NotifyIntervalMs)
        {
            return;
        }

        _lastNotifyTicks = elapsed;
        Stats = _sampler.Sample();
        StatsUpdated?.Invoke(this, Stats);
    }

    private long _lastFrameTicks;
    private double _lastNotifyTicks;
}
