using System.Buffers.Binary;
using System.Diagnostics;
using System.Text;
using AoiTwin.Core.Bus;
using AoiTwin.Core.Recording;

namespace AoiTwin.Core.Replay;

/// <summary>回放状态机。</summary>
public enum ReplayState
{
    /// <summary>未开始或已 Seek 等待启动。</summary>
    Idle,
    Playing,
    Paused,
    Completed,
}

/// <summary>单帧回放完成事件参数。</summary>
public sealed record ReplayFrameEventArgs(
    int FrameIndex,
    TwinTopic Topic,
    byte TypeId,
    DateTimeOffset OriginalTimestamp,
    DateTimeOffset ReplayTimestamp,
    ITwinMessage Message);

/// <summary>
/// 回放引擎（T3.1）：加载帧索引、按原始时间戳节奏回放、变速 0.5×–8×、
/// Seek 二分定位，并以回放 Publisher 身份将消息注入 IRealtimeHub。
///
/// 回放时钟（PRD §7-3）：帧的原始时间戳统一偏移为回放墙钟——
/// Play/Seek 时重置锚点，第 i 帧的回放时间 = 锚点墙钟 + (帧原始时间 − 锚点原始时间) / Speed。
/// 消息顺序与录制文件严格一致（按索引顺序回放，变速只压缩间隔）。
/// </summary>
public sealed class ReplayEngine : IDisposable
{
    public const double MinSpeed = 0.5;
    public const double MaxSpeed = 8.0;

    private readonly IRealtimeHub _hub;
    private readonly FileStream _stream;
    private readonly BinaryReader _reader;
    private readonly object _gate = new();

    private IReadOnlyList<FrameIndexEntry> _index;
    private long _startMs;
    private int _cursor;
    private double _speed = 1.0;
    private ReplayState _state = ReplayState.Idle;
    private CancellationTokenSource? _cts;
    private Task? _pump;
    private bool _disposed;

    /// <param name="filePath">.aotrec 录制文件。</param>
    /// <param name="hub">回放消息注入的总线。</param>
    public ReplayEngine(string filePath, IRealtimeHub hub)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(filePath);
        _hub = hub ?? throw new ArgumentNullException(nameof(hub));
        _index = RecordingReader.BuildIndex(filePath);
        _startMs = _index.Count > 0 ? _index[0].TimestampMs : 0;
        _stream = new FileStream(filePath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
        _reader = new BinaryReader(_stream, Encoding.UTF8, leaveOpen: true);
    }

    /// <summary>帧索引（有效前缀）。索引为空通常意味着文件损坏。</summary>
    public IReadOnlyList<FrameIndexEntry> Index => _index;

    public int FrameCount => _index.Count;

    /// <summary>下一帧索引（回放/暂停/Seek 的当前位置）。</summary>
    public int CurrentFrameIndex => Volatile.Read(ref _cursor);

    public ReplayState State
    {
        get { lock (_gate) return _state; }
    }

    /// <summary>当前已到原始时间线的时长（Seek 定位与 UI 时间轴用）。</summary>
    public TimeSpan CurrentElapsed => _cursor < _index.Count
        ? TimeSpan.FromMilliseconds(_index[_cursor].TimestampMs - _startMs)
        : TotalDuration;

    public TimeSpan TotalDuration => _index.Count > 0
        ? TimeSpan.FromMilliseconds(_index[^1].TimestampMs - _startMs)
        : TimeSpan.Zero;

    /// <summary>变速因子，0.5×–8×（即时生效于后续帧间隔）。</summary>
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

    /// <summary>每帧回放完成（订阅方在回放线程执行，不得阻塞）。</summary>
    public event Action<ReplayFrameEventArgs>? FrameReplayed;

    /// <summary>全部帧回放完成（Play 语义下自然播完才触发；Pause/Dispose 不触发）。</summary>
    public event Action? Completed;

    /// <summary>开始或继续回放。</summary>
    public void Play()
    {
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (_state == ReplayState.Playing)
            {
                return;
            }

            if (_state == ReplayState.Completed && _cursor >= _index.Count)
            {
                throw new InvalidOperationException("回放已完成，如需重播请先 Seek 回起点");
            }

            if (_cursor >= _index.Count)
            {
                _state = ReplayState.Completed;
                return;
            }

            _cts = new CancellationTokenSource();
            _state = ReplayState.Playing;
            _pump = PumpAsync(_cursor, _cts.Token);
        }
    }

    /// <summary>暂停：停在当前帧边界，Play 从下一帧继续。</summary>
    public void Pause()
    {
        Task? pump;
        lock (_gate)
        {
            if (_state != ReplayState.Playing)
            {
                return;
            }

            _state = ReplayState.Paused;
            _cts!.Cancel();
            pump = _pump;
            _pump = null;
        }

        try
        {
            pump?.Wait();
        }
        catch (AggregateException)
        {
            // 取消导致的任务终止，忽略
        }
    }

    /// <summary>
    /// Seek 到原始时间线的指定时长处（二分定位到首个该时长之后到达的帧）。
    /// 回放中调用会先暂停；回放时钟锚点随之重置（后续帧时间戳统一偏移为新的回放时钟）。
    /// </summary>
    public void Seek(TimeSpan elapsed)
    {
        if (elapsed < TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(elapsed));
        }

        Pause();
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            _cursor = FindFrameByElapsed(elapsed);
            _state = _cursor >= _index.Count ? ReplayState.Completed : ReplayState.Paused;
        }
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
            if (_state == ReplayState.Playing)
            {
                _cts?.Cancel();
                _state = ReplayState.Paused;
            }
        }

        try
        {
            _pump?.Wait(500);
        }
        catch (AggregateException)
        {
        }

        _cts?.Dispose();
        _reader.Dispose();
        _stream.Dispose();
    }

    private int FindFrameByElapsed(TimeSpan elapsed)
    {
        var target = (long)elapsed.TotalMilliseconds + _startMs;
        int lo = 0, hi = _index.Count;
        while (lo < hi)
        {
            var mid = lo + ((hi - lo) / 2);
            if (_index[mid].TimestampMs < target)
            {
                lo = mid + 1;
            }
            else
            {
                hi = mid;
            }
        }

        return lo;
    }

    private async Task PumpAsync(int startIndex, CancellationToken token)
    {
        var anchorOriginalMs = _index[startIndex].TimestampMs;
        var anchorReplay = DateTimeOffset.UtcNow;
        var stopwatch = Stopwatch.StartNew();

        for (var i = startIndex; i < _index.Count; i++)
        {
            if (token.IsCancellationRequested)
            {
                return;
            }

            var speed = Speed;
            var entry = _index[i];
            var targetMs = (entry.TimestampMs - anchorOriginalMs) / speed;
            var delay = targetMs - stopwatch.Elapsed.TotalMilliseconds;
            if (delay > 0)
            {
                try
                {
                    await Task.Delay(TimeSpan.FromMilliseconds(delay), token).ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                    return;
                }
            }

            ReplayFrame(i, entry, anchorOriginalMs, anchorReplay, speed);
            Volatile.Write(ref _cursor, i + 1);
        }

        var fireCompleted = false;
        lock (_gate)
        {
            if (_state == ReplayState.Playing)
            {
                _state = ReplayState.Completed;
                fireCompleted = true;
            }
        }

        if (fireCompleted)
        {
            Completed?.Invoke();
        }
    }

    private void ReplayFrame(int i, FrameIndexEntry entry, long anchorOriginalMs, DateTimeOffset anchorReplay, double speed)
    {
        lock (_stream)
        {
            _stream.Seek(entry.Offset, SeekOrigin.Begin);
            var header = _reader.ReadBytes(RecFileFormat.FrameHeaderSize);
            var payloadLen = BinaryPrimitives.ReadUInt32LittleEndian(header);
            var payload = _reader.ReadBytes((int)payloadLen);
            var message = RecordingReader.DeserializeFrame(entry.TypeId, payload);
            var replayTs = anchorReplay.AddMilliseconds((entry.TimestampMs - anchorOriginalMs) / speed);

            switch (message)
            {
                case AxisSnapshot m:
                {
                    var remapped = m with { Timestamp = replayTs };
                    _hub.Publish(entry.Topic, remapped);
                    FrameReplayed?.Invoke(new ReplayFrameEventArgs(i, entry.Topic, entry.TypeId, entry.Timestamp, replayTs, remapped));
                    break;
                }

                case StatusEvent m:
                {
                    var remapped = m with { Timestamp = replayTs };
                    _hub.Publish(entry.Topic, remapped);
                    FrameReplayed?.Invoke(new ReplayFrameEventArgs(i, entry.Topic, entry.TypeId, entry.Timestamp, replayTs, remapped));
                    break;
                }

                case OpticsEvent m:
                {
                    var remapped = m with { Timestamp = replayTs };
                    _hub.Publish(entry.Topic, remapped);
                    FrameReplayed?.Invoke(new ReplayFrameEventArgs(i, entry.Topic, entry.TypeId, entry.Timestamp, replayTs, remapped));
                    break;
                }

                case WaferResult m:
                {
                    var remapped = m with { Timestamp = replayTs };
                    _hub.Publish(entry.Topic, remapped);
                    FrameReplayed?.Invoke(new ReplayFrameEventArgs(i, entry.Topic, entry.TypeId, entry.Timestamp, replayTs, remapped));
                    break;
                }

                default:
                    throw new InvalidOperationException($"未登记的回放消息类型: {message.GetType().Name}");
            }
        }
    }
}
