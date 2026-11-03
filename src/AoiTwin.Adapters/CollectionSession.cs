using System.Diagnostics;
using AoiTwin.Core.Bus;
using AoiTwin.Core.Recording;

namespace AoiTwin.Adapters;

/// <summary>单路数据源的实时状态（T2.5 采集监控面板）。</summary>
public sealed record SourceSnapshot(
    string Name,
    AdapterHealth Health,
    long MessageCount,
    double RatePerSecond,
    DateTimeOffset? LastMessageAt)
{
    /// <summary>健康状态中文标签（面板直接绑定，避免 UI 层再写一次 switch）。</summary>
    public string HealthText => Health switch
    {
        AdapterHealth.Connected => "已连接",
        AdapterHealth.Connecting => "连接中",
        AdapterHealth.Reconnecting => "重连中",
        _ => "已停止",
    };

    /// <summary>是否处于故障态（断线重连或已停止）——面板据此触发告警条与黄色指示灯。</summary>
    public bool IsFaulted => Health is AdapterHealth.Reconnecting or AdapterHealth.Stopped;
}

/// <summary>单 Topic 的吞吐统计。</summary>
public sealed record TopicSnapshot(TwinTopic Topic, string DisplayName, long Count, double RatePerSecond);

/// <summary>录制实时状态。</summary>
public sealed record RecordingSnapshot(
    bool IsRecording,
    string? FilePath,
    long FrameCount,
    long DroppedFrameCount,
    long BytesWritten,
    bool PausedByDiskWatermark,
    string? Directory);

/// <summary>
/// 采集会话一次性快照：面板每秒取一份，视图模型不做任何计算（保证「显示 = 现场事实」）。
/// </summary>
public sealed record CollectionSnapshot(
    bool IsRunning,
    TimeSpan Elapsed,
    IReadOnlyList<SourceSnapshot> Sources,
    IReadOnlyList<TopicSnapshot> Topics,
    long TotalMessages,
    RecordingSnapshot Recording,
    double DiskUsedRatio)
{
    /// <summary>四路 Topic 速率之和（面板「总吞吐」）。</summary>
    public double AggregateRatePerSecond => Topics.Sum(t => t.RatePerSecond);
}

/// <summary>
/// 采集会话（T2.5 / NFR-05）：把「适配器 → Hub → 录制器」这条链路收成一个可启停的对象，
/// 同时提供面板所需的实时统计（逐源健康与吞吐、逐 Topic 吞吐、录制帧数、磁盘水位）。
///
/// 职责边界（与 M1 既有分层一致）：
/// - 适配器只负责连接/解析/发布，不认识录制与 UI；
/// - 本会话是唯一的「布线方」：订阅适配器 MessageEmitted → 计数 → 发布到 Hub；录制器只订阅 Hub；
/// - 统计计数全部在发布线程累加（Interlocked），面板线程只读快照，不做加锁读取热路径。
///
/// 故障隔离（NFR-05）：单路适配器断线只体现在该源 Health/速率上，其余通道继续计数与发布，
/// 会话本身从不因单源异常而停止。
/// </summary>
public sealed class CollectionSession : IDisposable
{
    /// <summary>面板刷新频率决定采样窗口下限：窗口过短会把速率抖成 0/满值。</summary>
    private const double MinSampleWindowSeconds = 0.25;

    private static readonly TwinTopic[] TopicOrder =
        [TwinTopic.Motion, TwinTopic.Status, TwinTopic.Optics, TwinTopic.Result];

    private readonly IRealtimeHub _hub;
    private readonly AdapterBase[] _adapters;
    private readonly RecordingOptions? _recordingOptions;
    private readonly Func<double> _diskUsageProbe;
    private readonly RateCounter[] _sourceCounters;
    private readonly long[] _lastMessageTicks;
    private readonly Dictionary<TwinTopic, RateCounter> _topicCounters = [];
    private readonly Stopwatch _clock = new();
    private readonly object _recordGate = new();
    private readonly List<IDisposable> _recordSubscriptions = [];

    private RecordingWriter? _writer;
    private string? _lastRecordingPath;
    private long _lastRecordingFrames;
    private long _lastRecordingBytes;
    private long _lastRecordingDropped;
    private bool _disposed;

    /// <param name="hub">消息出口总线（录制器与视图模型都从 Hub 取数）。</param>
    /// <param name="adapters">四路（或任意路）适配器；本会话负责起停与释放。</param>
    /// <param name="recordingOptions">录制参数；null 表示该会话不可录制（只做监控）。</param>
    public CollectionSession(
        IRealtimeHub hub,
        IEnumerable<AdapterBase> adapters,
        RecordingOptions? recordingOptions = null)
    {
        _hub = hub ?? throw new ArgumentNullException(nameof(hub));
        ArgumentNullException.ThrowIfNull(adapters);

        _adapters = [.. adapters];
        if (_adapters.Length == 0)
        {
            throw new ArgumentException("采集会话至少需要一路适配器", nameof(adapters));
        }

        var duplicated = _adapters.GroupBy(a => a.Name).FirstOrDefault(g => g.Count() > 1);
        if (duplicated is not null)
        {
            throw new ArgumentException($"数据源名称重复：{duplicated.Key}", nameof(adapters));
        }

        _recordingOptions = recordingOptions;
        _diskUsageProbe = recordingOptions?.DiskUsedRatioProvider
            ?? (() => DiskSpace.UsedRatio(recordingOptions?.Directory ?? AppContext.BaseDirectory));

        SourceNames = [.. _adapters.Select(a => a.Name)];
        _sourceCounters = [.. _adapters.Select(_ => new RateCounter())];
        _lastMessageTicks = new long[_adapters.Length];
        foreach (var topic in TopicOrder)
        {
            _topicCounters[topic] = new RateCounter();
        }

        // 布线在构造期完成：适配器未 Start 前不会产出消息，避免重复订阅
        for (var i = 0; i < _adapters.Length; i++)
        {
            var index = i;
            _adapters[i].MessageEmitted += (topic, message) => OnMessage(index, topic, message);
        }
    }

    /// <summary>数据源名称（面板固定行序，启动前后不变）。</summary>
    public IReadOnlyList<string> SourceNames { get; } = [];

    /// <summary>是否允许录制（构造时给了录制参数）。</summary>
    public bool CanRecord => _recordingOptions is not null;

    public bool IsRunning => _clock.IsRunning;

    /// <summary>启动采集：四路适配器并行连接，单路失败只影响自身（退避重连）。</summary>
    public void Start()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (_clock.IsRunning)
        {
            return;
        }

        _clock.Restart();
        ResetCounters();
        foreach (var adapter in _adapters)
        {
            adapter.Start();
        }
    }

    /// <summary>停止采集：先停适配器再停录制，保证录制文件拿到 Footer（可校验）。</summary>
    public void Stop()
    {
        if (_disposed)
        {
            return;
        }

        foreach (var adapter in _adapters)
        {
            adapter.Stop();
        }

        StopRecording();
        _clock.Stop();
    }

    /// <summary>开始录制（幂等）。录制订阅 Hub，因此录到的是「已发布」的帧，与回放口径一致。</summary>
    public bool StartRecording()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (_recordingOptions is null)
        {
            throw new InvalidOperationException("该采集会话未配置录制参数");
        }

        lock (_recordGate)
        {
            if (_writer is not null)
            {
                return false;
            }

            var writer = new RecordingWriter(_recordingOptions);
            _writer = writer;
            _lastRecordingPath = null;
            _lastRecordingFrames = 0;
            _lastRecordingBytes = 0;
            _lastRecordingDropped = 0;
            _recordSubscriptions.Add(_hub.Subscribe<AxisSnapshot>(
                TwinTopic.Motion, m => writer.Write(TwinTopic.Motion, m)));
            _recordSubscriptions.Add(_hub.Subscribe<StatusEvent>(
                TwinTopic.Status, m => writer.Write(TwinTopic.Status, m)));
            _recordSubscriptions.Add(_hub.Subscribe<OpticsEvent>(
                TwinTopic.Optics, m => writer.Write(TwinTopic.Optics, m)));
            _recordSubscriptions.Add(_hub.Subscribe<WaferResult>(
                TwinTopic.Result, m => writer.Write(TwinTopic.Result, m)));
            return true;
        }
    }

    /// <summary>停止录制并落 Footer；返回本次录制的文件路径（未产生帧时为 null）。</summary>
    public string? StopRecording()
    {
        lock (_recordGate)
        {
            foreach (var subscription in _recordSubscriptions)
            {
                subscription.Dispose();
            }

            _recordSubscriptions.Clear();

            var writer = _writer;
            _writer = null;
            if (writer is null)
            {
                return null;
            }

            // 停止后统计值仍需在面板上可见（「本次录制结果」），故在释放前留档
            _lastRecordingPath = writer.CurrentFilePath;
            _lastRecordingFrames = writer.TotalFrameCount;
            _lastRecordingBytes = writer.TotalBytesWritten;
            _lastRecordingDropped = writer.DroppedFrameCount;

            var path = writer.CurrentFilePath;
            writer.Dispose();
            return path;
        }
    }

    /// <summary>取一份统计快照（任意线程可调用；只读，不加锁读热路径）。</summary>
    public CollectionSnapshot Snapshot()
    {
        var sources = new List<SourceSnapshot>(_adapters.Length);
        for (var i = 0; i < _adapters.Length; i++)
        {
            var (count, rate) = _sourceCounters[i].Sample();
            var ticks = Interlocked.Read(ref _lastMessageTicks[i]);
            sources.Add(new SourceSnapshot(
                _adapters[i].Name,
                _adapters[i].Health,
                count,
                rate,
                ticks == 0 ? null : new DateTimeOffset(ticks, TimeSpan.Zero)));
        }

        var topics = new List<TopicSnapshot>(TopicOrder.Length);
        long total = 0;
        foreach (var topic in TopicOrder)
        {
            var (count, rate) = _topicCounters[topic].Sample();
            total += count;
            topics.Add(new TopicSnapshot(topic, DisplayName(topic), count, rate));
        }

        RecordingWriter? writer;
        string? lastPath;
        long lastFrames, lastBytes, lastDropped;
        lock (_recordGate)
        {
            writer = _writer;
            lastPath = _lastRecordingPath;
            lastFrames = _lastRecordingFrames;
            lastBytes = _lastRecordingBytes;
            lastDropped = _lastRecordingDropped;
        }

        var recording = new RecordingSnapshot(
            writer is not null,
            writer?.CurrentFilePath ?? lastPath,
            writer?.TotalFrameCount ?? lastFrames,
            writer?.DroppedFrameCount ?? lastDropped,
            writer?.TotalBytesWritten ?? lastBytes,
            writer?.PausedByDiskWatermark ?? false,
            _recordingOptions?.Directory);

        return new CollectionSnapshot(
            IsRunning,
            _clock.Elapsed,
            sources,
            topics,
            total,
            recording,
            _diskUsageProbe());
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        Stop();
        _disposed = true;

        foreach (var adapter in _adapters)
        {
            adapter.Dispose();
        }
    }

    /// <summary>四类消息封闭集合的显式分发（新增类型须在此登记，编译期不会漏，但运行期会抛）。</summary>
    private void OnMessage(int sourceIndex, TwinTopic topic, ITwinMessage message)
    {
        _sourceCounters[sourceIndex].Increment();
        Interlocked.Exchange(ref _lastMessageTicks[sourceIndex], message.Timestamp.UtcTicks);

        if (_topicCounters.TryGetValue(topic, out var counter))
        {
            counter.Increment();
        }

        switch (message)
        {
            case AxisSnapshot m:
                _hub.Publish(topic, m);
                break;
            case StatusEvent m:
                _hub.Publish(topic, m);
                break;
            case OpticsEvent m:
                _hub.Publish(topic, m);
                break;
            case WaferResult m:
                _hub.Publish(topic, m);
                break;
            default:
                throw new NotSupportedException($"未登记的消息类型: {message.GetType().Name}");
        }
    }

    private void ResetCounters()
    {
        foreach (var counter in _sourceCounters)
        {
            counter.Reset();
        }

        foreach (var counter in _topicCounters.Values)
        {
            counter.Reset();
        }

        Array.Clear(_lastMessageTicks);
    }

    private static string DisplayName(TwinTopic topic) => topic switch
    {
        TwinTopic.Motion => "Motion · 运动",
        TwinTopic.Status => "Status · 状态",
        TwinTopic.Optics => "Optics · 光学",
        TwinTopic.Result => "Result · 结果",
        _ => topic.ToString(),
    };

    /// <summary>
    /// 滑动速率计数器：计数在数据线程累加（Interlocked），采样在 UI 线程按窗口计算。
    /// 窗口 &lt; 0.25s 时沿用上次速率——面板 1Hz 刷新下这是稳定值，不会被单帧抖动放大。
    /// </summary>
    private sealed class RateCounter
    {
        private readonly object _gate = new();
        private long _count;
        private long _sampledCount;
        private long _sampledTicks = Stopwatch.GetTimestamp();
        private double _rate;

        public void Increment() => Interlocked.Increment(ref _count);

        public void Reset()
        {
            Interlocked.Exchange(ref _count, 0);
            lock (_gate)
            {
                _sampledCount = 0;
                _sampledTicks = Stopwatch.GetTimestamp();
                _rate = 0;
            }
        }

        public (long Count, double Rate) Sample()
        {
            var count = Interlocked.Read(ref _count);
            lock (_gate)
            {
                var now = Stopwatch.GetTimestamp();
                var elapsed = (now - _sampledTicks) / (double)Stopwatch.Frequency;
                if (elapsed >= MinSampleWindowSeconds)
                {
                    _rate = (count - _sampledCount) / elapsed;
                    _sampledCount = count;
                    _sampledTicks = now;
                }

                return (count, _rate);
            }
        }
    }
}
