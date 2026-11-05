using AoiTwin.Core.Bus;

namespace AoiTwin.Core.Persistence;

/// <summary>
/// 部件健康档案（TB.4）：M3 全部预测模型的输入底座。
/// 关键设计：档案只保存**可累计的原始量**（行程、亮灯时长、报警次数、节拍统计），
/// 不保存派生评分——评分属于分析层，随时可按新权重重算；把评分存进来就等于把历史结论固化。
/// </summary>
public sealed record PartHealthProfile(
    string PartId,
    long AxisMessageCount,
    double TravelMm,
    int AlarmCount,
    int WarningCount,
    double LightOnHours,
    int WaferCount,
    double MeanTactSec,
    double LastTactSec,
    DateTimeOffset? LastSeen)
{
    public static PartHealthProfile Empty(string partId) => new(partId, 0, 0, 0, 0, 0, 0, 0, 0, null);

    /// <summary>累计行程（km），报告与评分因子用。</summary>
    public double TravelKm => TravelMm / 1_000_000.0;

    /// <summary>是否有任何观测（全部为 0 的部件不应参与评分，避免「没数据 = 满分/零分」的误导）。</summary>
    public bool HasObservations => AxisMessageCount > 0 || AlarmCount > 0 || WarningCount > 0 || WaferCount > 0 || LightOnHours > 0;
}

/// <summary>
/// 健康档案聚合器（TB.4）：订阅 Hub，按 PartId 累计行程/报警/亮灯时长/节拍。
///
/// 两条工程约束：
/// 1) **只用消息自带时间戳**累计时长（不用墙钟）——回灌历史与实时数据因此走同一条路径，
///    回放 8 倍速不会把亮灯时长算成 8 倍；
/// 2) **乱序与时钟回跳安全**：时间差为负或超过 <see cref="MaxGapForAccumulation"/> 时不计入，
///    避免设备对时/跨班次导致时长虚增。
/// </summary>
public sealed class PartHealthAggregator : IDisposable
{
    /// <summary>单次时长累计上限：超过视为数据断档（设备停机/对时），不把断档算成亮灯。</summary>
    public static readonly TimeSpan MaxGapForAccumulation = TimeSpan.FromSeconds(10);

    private readonly object _gate = new();
    private readonly Dictionary<string, Mutable> _profiles = new(StringComparer.Ordinal);
    private readonly Dictionary<(string PartId, string Axis), double> _lastPositions = [];
    private readonly Dictionary<string, DateTimeOffset> _lastLightTimestamps = new(StringComparer.Ordinal);
    private readonly List<IDisposable> _subscriptions = [];
    private bool _disposed;

    public PartHealthAggregator(IRealtimeHub hub)
    {
        ArgumentNullException.ThrowIfNull(hub);
        _subscriptions.Add(hub.Subscribe<AxisSnapshot>(TwinTopic.Motion, m => Apply(TwinTopic.Motion, m)));
        _subscriptions.Add(hub.Subscribe<StatusEvent>(TwinTopic.Status, m => Apply(TwinTopic.Status, m)));
        _subscriptions.Add(hub.Subscribe<OpticsEvent>(TwinTopic.Optics, m => Apply(TwinTopic.Optics, m)));
        _subscriptions.Add(hub.Subscribe<WaferResult>(TwinTopic.Result, m => Apply(TwinTopic.Result, m)));
    }

    /// <summary>供历史回灌重建档案（不依赖 Hub 的实时订阅）。</summary>
    public PartHealthAggregator()
    {
    }

    /// <summary>当前档案快照（按 PartId 升序）。</summary>
    public IReadOnlyList<PartHealthProfile> Snapshot()
    {
        lock (_gate)
        {
            return _profiles
                .OrderBy(kv => kv.Key, StringComparer.Ordinal)
                .Select(kv => kv.Value.ToProfile(kv.Key))
                .ToList();
        }
    }

    public PartHealthProfile? Get(string partId)
    {
        lock (_gate)
        {
            return _profiles.TryGetValue(partId, out var mutable) ? mutable.ToProfile(partId) : null;
        }
    }

    /// <summary>
    /// 应用一条消息（实时订阅与历史回灌共用）。未知类型忽略——分析层不得因契约外消息崩溃。
    /// </summary>
    public void Apply(TwinTopic topic, ITwinMessage message)
    {
        ArgumentNullException.ThrowIfNull(message);
        lock (_gate)
        {
            var partId = message.PartId;
            if (!_profiles.TryGetValue(partId, out var profile))
            {
                profile = new Mutable();
                _profiles[partId] = profile;
            }

            profile.Observe(message.Timestamp);

            switch (message)
            {
                case AxisSnapshot axis:
                    ObserveAxis(profile, axis);
                    break;

                case StatusEvent status:
                    if (status.Severity == Severity.Alarm)
                    {
                        profile.AlarmCount++;
                    }
                    else if (status.Severity == Severity.Warning)
                    {
                        profile.WarningCount++;
                    }

                    break;

                case OpticsEvent optics when optics.Kind is OpticsKind.LightBrightness or OpticsKind.LightChannel:
                    ObserveLight(profile, partId, optics);
                    break;

                case WaferResult wafer:
                    profile.ObserveTact(wafer.Tact.TotalSeconds);
                    break;
            }
        }
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        foreach (var subscription in _subscriptions)
        {
            subscription.Dispose();
        }

        _subscriptions.Clear();
    }

    private void ObserveAxis(Mutable profile, AxisSnapshot axis)
    {
        profile.AxisMessageCount++;
        var key = (axis.PartId, axis.AxisName);
        if (_lastPositions.TryGetValue(key, out var last))
        {
            // Position 单位为 µm（与示教坐标一致），累计换成 mm
            profile.TravelMm += Math.Abs(axis.Position - last) / 1000.0;
        }

        _lastPositions[key] = axis.Position;
    }

    private void ObserveLight(Mutable profile, string partId, OpticsEvent optics)
    {
        if (optics.Value <= 0)
        {
            _lastLightTimestamps.Remove(partId);
            return;
        }

        if (_lastLightTimestamps.TryGetValue(partId, out var last))
        {
            var gap = optics.Timestamp - last;
            if (gap > TimeSpan.Zero && gap <= MaxGapForAccumulation)
            {
                profile.LightOnHours += gap.TotalHours;
            }
        }

        _lastLightTimestamps[partId] = optics.Timestamp;
    }

    private sealed class Mutable
    {
        private double _tactMean;
        private double _tactM2;

        public long AxisMessageCount;
        public double TravelMm;
        public int AlarmCount;
        public int WarningCount;
        public double LightOnHours;
        public int WaferCount;
        public double LastTactSec;
        public DateTimeOffset? LastSeen;

        public void Observe(DateTimeOffset timestamp)
        {
            if (LastSeen is null || timestamp > LastSeen)
            {
                LastSeen = timestamp;
            }
        }

        public void ObserveTact(double seconds)
        {
            WaferCount++;
            LastTactSec = seconds;

            // Welford：单遍均值/方差，回灌百万片也不会累积浮点误差
            var delta = seconds - _tactMean;
            _tactMean += delta / WaferCount;
            _tactM2 += delta * (seconds - _tactMean);
        }

        public PartHealthProfile ToProfile(string partId) => new(
            partId,
            AxisMessageCount,
            TravelMm,
            AlarmCount,
            WarningCount,
            LightOnHours,
            WaferCount,
            WaferCount == 0 ? 0 : _tactMean,
            LastTactSec,
            LastSeen);
    }
}
