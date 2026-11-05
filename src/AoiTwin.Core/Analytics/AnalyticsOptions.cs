using AoiTwin.Core.Bus;

namespace AoiTwin.Core.Analytics;

/// <summary>
/// 分析参数（全部可配置，支持热加载语义：每次分析重新读取）。
/// 所有阈值都写在这里而不是散落在模型里——现场调参不需要重新编译，也不会出现
/// 「改了这个模型忘了那个模型」的口径漂移。
/// </summary>
public sealed record AnalyticsOptions
{
    /// <summary>分析时间窗（回测与实时共用的默认窗口）。</summary>
    public TimeSpan Window { get; init; } = TimeSpan.FromDays(30);

    /// <summary>调度周期（PRD：5 分钟周期 + 事件触发）。</summary>
    public TimeSpan SchedulerPeriod { get; init; } = TimeSpan.FromMinutes(5);

    /// <summary>同一预警的重复抑制窗口（避免每 5 分钟重复推同一条到 3D）。</summary>
    public TimeSpan RepeatSuppression { get; init; } = TimeSpan.FromMinutes(30);

    // ---- 光源衰减（TB.7） ----

    /// <summary>额定亮度（0–100 归一化口径）；≤0 表示用窗口内首个样本作为基准。</summary>
    public double LightRatedValue { get; init; } = 0;

    /// <summary>预警亮度比例（跌至额定的该比例即进入预警）。</summary>
    public double LightWarningFraction { get; init; } = 0.8;

    /// <summary>严重亮度比例。</summary>
    public double LightCriticalFraction { get; init; } = 0.6;

    /// <summary>提前量门槛：预测跌到预警线 ≤ 该天数 → Warning（PRD 要求 ≥ 7 天提前量）。</summary>
    public int LightWarningHorizonDays { get; init; } = 7;

    /// <summary>预测跌到严重线 ≤ 该天数 → Critical。</summary>
    public int LightCriticalHorizonDays { get; init; } = 3;

    /// <summary>回归最少样本数（少于该数不发预测——宁可不报，不可乱报）。</summary>
    public int LightMinSamples { get; init; } = 10;

    // ---- 轴磨损（TB.8） ----

    /// <summary>重复定位偏差的 z 分数门槛（相对滚动基线）。</summary>
    public double AxisWearSigmaThreshold { get; init; } = 3.0;

    /// <summary>偏差漂移速率门槛（µm/天），超过即判严重。</summary>
    public double AxisDriftUmPerDay { get; init; } = 2.0;

    /// <summary>判定「到位置保持段」的速度阈值（µm/s）：低于此速度视为静止，其位置离散度即重复定位偏差。</summary>
    public double AxisHoldVelocityUmPerSec { get; init; } = 50;

    /// <summary>轴磨损最少样本数。</summary>
    public int AxisMinSamples { get; init; } = 20;

    // ---- 节拍劣化（TB.9） ----

    /// <summary>节拍劣化百分比门槛（末段相对基线）。</summary>
    public double TactDegradationPercent { get; init; } = 5.0;

    /// <summary>节拍分析最少样本数。</summary>
    public int TactMinSamples { get; init; } = 20;

    // ---- 健康评分（TB.6） ----

    public HealthWeights Weights { get; init; } = new();
}

/// <summary>
/// 健康评分权重（TB.6，FR-M3-05「评分规则透明可配置」）。
/// 扣分制而非加分制：满分 100 起步，每个不利证据按权重扣——这样「为什么扣分」天然可解释，
/// 也让新证据（新模型）可以零改动接入评分。
/// </summary>
public sealed record HealthWeights(
    double AlarmPenalty = 25,
    double WarningPenalty = 5,
    double TravelPenaltyPerKm = 2,
    double LightPenaltyPerHour = 0.02,
    double TactPenaltyPerPercent = 3,
    double PredictorPenaltyWarning = 8,
    double PredictorPenaltyCritical = 20)
{
    public static HealthWeights Default { get; } = new();
}

/// <summary>
/// 默认分析上下文（TB.5/TB.7）：档案来自聚合器，序列来自时序库（可空 → 纯档案模式）。
///
/// 为什么把「查库」放在上下文而不是预测器里：一个预测器只应该关心算法；
/// 查库窗口、Topic 过滤、排序这些口径统一在这里，避免三个模型各写一套查询导致回测结果不可比。
/// </summary>
public sealed class AnalyticsContext : IAnalyticsContext
{
    private readonly Persistence.ITimeSeriesStore? _store;
    private readonly TimeSpan? _windowOverride;

    public AnalyticsContext(
        DateTimeOffset now,
        IReadOnlyList<Persistence.PartHealthProfile> profiles,
        AnalyticsOptions? options = null,
        Persistence.ITimeSeriesStore? store = null,
        TimeSpan? windowOverride = null)
    {
        Now = now;
        Profiles = profiles ?? [];
        Options = options ?? new AnalyticsOptions();
        _store = store;
        _windowOverride = windowOverride;
    }

    public DateTimeOffset Now { get; }

    public IReadOnlyList<Persistence.PartHealthProfile> Profiles { get; }

    public AnalyticsOptions Options { get; }

    private TimeSpan Window => _windowOverride ?? Options.Window;

    public IReadOnlyList<LightSample> LightSamples(TimeSpan window)
    {
        if (_store is null)
        {
            return [];
        }

        var from = Now - window;
        return _store.Query(new Persistence.TimeSeriesQuery(from, Now, TwinTopic.Optics))
            .Select(r => (Record: r, Message: r.ToMessage()))
            .OfType<(Persistence.TimeSeriesRecord Record, ITwinMessage Message)>()
            .Where(x => x.Message is OpticsEvent o && o.Kind is OpticsKind.LightBrightness or OpticsKind.LightChannel)
            .Select(x =>
            {
                var optics = (OpticsEvent)x.Message;
                return new LightSample(x.Record.Timestamp, optics.PartId, optics.Channel, optics.Value);
            })
            .OrderBy(s => s.Timestamp)
            .ToList();
    }

    public IReadOnlyList<AxisSample> AxisSamples(TimeSpan window)
    {
        if (_store is null)
        {
            return [];
        }

        var from = Now - window;
        return _store.Query(new Persistence.TimeSeriesQuery(from, Now, TwinTopic.Motion))
            .Select(r => (Record: r, Message: r.ToMessage()))
            .Where(x => x.Message is AxisSnapshot)
            .Select(x =>
            {
                var axis = (AxisSnapshot)x.Message;
                return new AxisSample(x.Record.Timestamp, axis.PartId, axis.AxisName, axis.Position, axis.Velocity);
            })
            .OrderBy(s => s.Timestamp)
            .ToList();
    }

    public IReadOnlyList<TactSample> TactSamples(TimeSpan window)
    {
        if (_store is null)
        {
            return [];
        }

        var from = Now - window;
        return _store.Query(new Persistence.TimeSeriesQuery(from, Now, TwinTopic.Result))
            .Select(r => (Record: r, Message: r.ToMessage()))
            .Where(x => x.Message is WaferResult)
            .Select(x =>
            {
                var wafer = (WaferResult)x.Message;
                return new TactSample(x.Record.Timestamp, StationOf(wafer.WaferId), wafer.Tact.TotalSeconds);
            })
            .OrderBy(s => s.Timestamp)
            .ToList();
    }

    /// <summary>
    /// 从 WaferId 解析站点：约定 <c>&lt;STATION&gt;-&lt;序号&gt;</c>（如 <c>ST02-000123</c>）；
    /// 不含 '-' 时归入 "ALL"。真机接入时站点应来自设备台账而非字符串解析（见 M3 真机验证清单）。
    /// </summary>
    public static string StationOf(string waferId)
    {
        if (string.IsNullOrWhiteSpace(waferId))
        {
            return "ALL";
        }

        var dash = waferId.IndexOf('-');
        return dash > 0 ? waferId[..dash] : "ALL";
    }
}
