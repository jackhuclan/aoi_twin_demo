using System.Diagnostics;
using AoiTwin.Core.Analytics;
using AoiTwin.Core.Bus;

namespace AoiTwin.Core.Persistence;

/// <summary>历史回放状态（与文件回放同构，UI 可复用同一套按钮语义）。</summary>
public enum HistoryReplayState
{
    Idle,
    Playing,
    Paused,
    Completed,
}

/// <summary>历史回放进度。</summary>
public sealed record HistoryReplayProgress(
    HistoryReplayState State,
    int FrameIndex,
    int FrameCount,
    TimeSpan Position,
    TimeSpan Total,
    TwinTopic? LastTopic);

/// <summary>
/// 历史回灌回放（TB.3）：按日期/条件从时序库取片段，以与文件回放**相同的注入通道**
/// 推回 <see cref="IRealtimeHub"/>，因此 3D 场景、联动、分析层无需知道数据来自文件还是库。
///
/// 时间语义与 <c>ReplayEngine</c> 对齐：原始时间戳统一偏移为回放墙钟，
/// 第 i 条的墙钟 = 锚点 + (原始 − 锚点) / Speed。变速只压缩间隔，不改变顺序。
///
/// 为什么先全量加载再播：历史片段的索引价值低于文件（无 CRC/无帧索引），
/// 30 天量级的一次查询在本机为秒级；更长的区间应由调用方先缩小范围（UI 上按日期选择），
/// 这也避免回放过程中反复读盘导致的抖动。
/// </summary>
public sealed class TimeSeriesReplayEngine : IDisposable
{
    public const double MinSpeed = 0.5;
    public const double MaxSpeed = 8.0;

    private readonly ITimeSeriesStore _store;
    private readonly IRealtimeHub _hub;
    private readonly object _gate = new();

    private IReadOnlyList<TimeSeriesRecord> _frames = [];
    private int _cursor;
    private double _speed = 1.0;
    private HistoryReplayState _state = HistoryReplayState.Idle;
    private CancellationTokenSource? _cts;
    private Task? _pump;
    private long _published;
    private bool _disposed;

    public TimeSeriesReplayEngine(ITimeSeriesStore store, IRealtimeHub hub)
    {
        _store = store ?? throw new ArgumentNullException(nameof(store));
        _hub = hub ?? throw new ArgumentNullException(nameof(hub));
    }

    public int FrameCount => _frames.Count;

    public int CurrentIndex => Volatile.Read(ref _cursor);

    public long PublishedCount => Interlocked.Read(ref _published);

    public HistoryReplayState State
    {
        get { lock (_gate) return _state; }
    }

    public double Speed
    {
        get { lock (_gate) return _speed; }
        set
        {
            if (value is < MinSpeed or > MaxSpeed)
            {
                throw new ArgumentOutOfRangeException(nameof(value), $"变速范围 {MinSpeed}×–{MaxSpeed}×");
            }

            lock (_gate) _speed = value;
        }
    }

    public TimeSpan TotalDuration => _frames.Count > 1
        ? _frames[^1].Timestamp - _frames[0].Timestamp
        : TimeSpan.Zero;

    public TimeSpan Position => _cursor < _frames.Count && _frames.Count > 1
        ? _frames[_cursor].Timestamp - _frames[0].Timestamp
        : TotalDuration;

    public event Action<HistoryReplayProgress>? Progress;

    public event Action? Completed;

    /// <summary>按条件加载片段；返回帧数。加载会重置游标与状态。</summary>
    public int Load(TimeSeriesQuery query)
    {
        ArgumentNullException.ThrowIfNull(query);
        Pause();

        var frames = _store.Query(query).OrderBy(r => r.Timestamp).ToList();
        lock (_gate)
        {
            _frames = frames;
            _cursor = 0;
            _state = frames.Count == 0 ? HistoryReplayState.Completed : HistoryReplayState.Idle;
        }

        RaiseProgress(null);
        return frames.Count;
    }

    public void Play()
    {
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (_state == HistoryReplayState.Playing || _cursor >= _frames.Count)
            {
                return;
            }

            _cts?.Dispose();
            _cts = new CancellationTokenSource();
            _state = HistoryReplayState.Playing;
            _pump = PumpAsync(_cursor, _cts.Token);
        }
    }

    public void Pause()
    {
        Task? pump;
        lock (_gate)
        {
            if (_state != HistoryReplayState.Playing)
            {
                return;
            }

            _state = HistoryReplayState.Paused;
            _cts?.Cancel();
            pump = _pump;
            _pump = null;
        }

        try
        {
            pump?.Wait();
        }
        catch (AggregateException)
        {
        }
    }

    /// <summary>跳转到相对起点的时长（二分定位到首个 ≥ 目标的记录）。</summary>
    public void Seek(TimeSpan position)
    {
        if (position < TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(position));
        }

        Pause();
        lock (_gate)
        {
            if (_frames.Count == 0)
            {
                _cursor = 0;
                return;
            }

            var target = _frames[0].Timestamp + position;
            int lo = 0, hi = _frames.Count;
            while (lo < hi)
            {
                var mid = lo + ((hi - lo) / 2);
                if (_frames[mid].Timestamp < target)
                {
                    lo = mid + 1;
                }
                else
                {
                    hi = mid;
                }
            }

            _cursor = Math.Min(lo, _frames.Count);
            _state = _cursor >= _frames.Count ? HistoryReplayState.Completed : HistoryReplayState.Paused;
        }

        RaiseProgress(null);
    }

    /// <summary>
    /// 单步：注入当前帧并前进一条（测试与逐帧复核用；不依赖墙钟）。
    /// 返回 false 表示已到末尾。
    /// </summary>
    public bool Step()
    {
        int index;
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (_cursor >= _frames.Count)
            {
                return false;
            }

            index = _cursor;
            _cursor++;
            if (_cursor >= _frames.Count)
            {
                _state = HistoryReplayState.Completed;
            }
        }

        PublishFrame(index, TimelineAnchor(index));
        RaiseProgress(_frames[index].Topic);
        return true;
    }

    public void Dispose()
    {
        lock (_gate)
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
            _cts?.Cancel();
        }

        try
        {
            _pump?.Wait(500);
        }
        catch (AggregateException)
        {
        }

        _cts?.Dispose();
    }

    private async Task PumpAsync(int startIndex, CancellationToken token)
    {
        var anchor = TimelineAnchor(startIndex);
        var stopwatch = Stopwatch.StartNew();

        for (var i = startIndex; i < _frames.Count; i++)
        {
            if (token.IsCancellationRequested)
            {
                return;
            }

            var speed = Speed;
            var delayMs = ((_frames[i].Timestamp - anchor.Original).TotalMilliseconds / speed) - stopwatch.Elapsed.TotalMilliseconds;
            if (delayMs > 0)
            {
                try
                {
                    await Task.Delay(TimeSpan.FromMilliseconds(delayMs), token).ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                    return;
                }
            }

            PublishFrame(i, anchor);
            Volatile.Write(ref _cursor, i + 1);
        }

        var fire = false;
        lock (_gate)
        {
            if (_state == HistoryReplayState.Playing)
            {
                _state = HistoryReplayState.Completed;
                fire = true;
            }
        }

        RaiseProgress(null);
        if (fire)
        {
            Completed?.Invoke();
        }
    }

    private (DateTimeOffset Original, DateTimeOffset Wall) TimelineAnchor(int index)
    {
        var original = _frames.Count > 0 ? _frames[Math.Min(index, _frames.Count - 1)].Timestamp : DateTimeOffset.UtcNow;
        return (original, DateTimeOffset.UtcNow);
    }

    private void PublishFrame(int index, (DateTimeOffset Original, DateTimeOffset Wall) anchor)
    {
        var record = _frames[index];
        var wall = anchor.Wall.AddMilliseconds((record.Timestamp - anchor.Original).TotalMilliseconds / Speed);
        var message = record.ToMessage();
        var remapped = message switch
        {
            AxisSnapshot m => m with { Timestamp = wall },
            StatusEvent m => m with { Timestamp = wall },
            OpticsEvent m => m with { Timestamp = wall },
            WaferResult m => m with { Timestamp = wall },
            AlertEvent m => m with { Timestamp = wall },
            _ => message,
        };

        _hub.Publish(record.Topic, remapped);
        Interlocked.Increment(ref _published);
    }

    private void RaiseProgress(TwinTopic? lastTopic)
    {
        HistoryReplayProgress progress;
        lock (_gate)
        {
            progress = new HistoryReplayProgress(_state, _cursor, _frames.Count, Position, TotalDuration, lastTopic);
        }

        Progress?.Invoke(progress);
    }
}
