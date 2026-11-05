using System.IO;
using AoiTwin.Core.Analytics;
using AoiTwin.Core.Analytics.Export;
using AoiTwin.Core.Bus;
using AoiTwin.Core.Persistence;

namespace AoiTwin.App.Services;

/// <summary>分析运行时参数（App 层组装用）。</summary>
/// <param name="StoreRoot">时序库根目录（按 UTC 天分区）。</param>
/// <param name="ExportDirectory">导出目录（健康档案 / 预警历史）。</param>
/// <param name="OptionsFilePath">分析参数文件；缺省或读取失败时用代码默认值。</param>
/// <param name="RetentionDays">保留天数（早于现在的分区在启动时清理）。</param>
/// <param name="AutoStart">是否随构造启动周期分析。</param>
public sealed record AnalyticsRuntimeOptions(
    string StoreRoot,
    string ExportDirectory,
    string? OptionsFilePath = null,
    int RetentionDays = 90,
    bool AutoStart = true);

/// <summary>健康页一次读取的快照（UI 只做格式化，不再触发 IO）。</summary>
public sealed record AnalyticsSnapshot(
    PersistenceStats Persistence,
    int Partitions,
    long StoreBytes,
    bool SchedulerRunning,
    DateTimeOffset? LastRunAt,
    IReadOnlyList<PredictorRun> Runs,
    IReadOnlyList<HealthScore> Scores,
    IReadOnlyList<AlertEvent> Alerts,
    long PublishedCount,
    AnalyticsOptions Options,
    string? OptionsError,
    string? LastError)
{
    public IEnumerable<AlertFinding> Findings => Runs.SelectMany(r => r.Findings);

    public bool HasFindings => Runs.Any(r => r.Findings.Count > 0);

    public bool FailedPredictor => Runs.Any(r => r.Failed);
}

/// <summary>
/// 分析运行时（TB.5–TB.16 的 App 侧组装）：把「时序库 + Sink + 档案聚合 + 预测器 + 调度 + 导出」
/// 收在一个可替换的入口后面。
///
/// 为什么单独一层而不直接塞进视图模型：
/// 1) <b>视图模型不该拥有定时器、磁盘与线程</b>——健康页每次刷新要能随手取快照，
///    而不是顺手把库读一遍（读库发生在 <see cref="AnalyzeNow"/>，明确、可计时）；
/// 2) <b>测试需要能关掉周期调度</b>（<see cref="AnalyticsRuntimeOptions.AutoStart"/>），
///    否则用例会被 5 分钟定时器拖成偶发；
/// 3) 异常边界只在这一层破：Sink 与分析都是 Hub 的普通订阅方，任何失败只进
///    <see cref="AnalyticsSnapshot.LastError"/>，不冒到实时链路（NFR-22）。
/// </summary>
public sealed class AnalyticsRuntime : IDisposable
{
    /// <summary>预警历史保留条数（健康页时间线只用于「最近发生了什么」）。</summary>
    public const int MaxAlertHistory = 200;

    private readonly object _gate = new();
    private readonly List<AlertEvent> _history = [];
    private readonly ITimeSeriesStore _store;
    private readonly PersistenceSink _sink;
    private readonly PartHealthAggregator _aggregator;
    private readonly AnalyticsOptionsProvider _optionsProvider;
    private readonly PredictorRegistry _registry;
    private readonly HealthScorer _scorer;
    private readonly AlertScheduler _scheduler;
    private readonly AnalyticsExportService _exporter;
    private readonly string _exportDirectory;

    private DateTimeOffset? _lastRunAt;
    private string? _lastError;
    private bool _disposed;

    public AnalyticsRuntime(IRealtimeHub hub, AnalyticsRuntimeOptions options, ITimeSeriesStore? store = null)
    {
        ArgumentNullException.ThrowIfNull(hub);
        ArgumentNullException.ThrowIfNull(options);
        ArgumentException.ThrowIfNullOrWhiteSpace(options.StoreRoot);

        _exportDirectory = options.ExportDirectory;
        Directory.CreateDirectory(_exportDirectory);

        // store 可替换：默认按天分区 JSONL；现场换 SQLite 只换这一处（NFR-22 存储可替换）
        _store = store ?? new FileTimeSeriesStore(options.StoreRoot);
        _optionsProvider = new AnalyticsOptionsProvider(options.OptionsFilePath);
        _sink = new PersistenceSink(_store, hub, new PersistenceOptions(_store.RootPath, "AOI-01"));
        _aggregator = new PartHealthAggregator(hub);
        _scorer = new HealthScorer(() => _optionsProvider.Current.Weights);

        _registry = new PredictorRegistry();
        _registry.RegisterRange([new LightDecayPredictor(), new AxisWearPredictor(), new TactTrendAnalyzer()]);

        // 上下文每次分析重建：Options 走 Provider（热加载），档案取当时快照，绝不缓存窗口数据
        _scheduler = new AlertScheduler(
            hub,
            _registry,
            () => new AnalyticsContext(
                DateTimeOffset.UtcNow,
                _aggregator.Snapshot(),
                _optionsProvider.Current,
                _store),
            _optionsProvider.Current);

        _scheduler.AlertRaised += OnAlertRaised;
        _exporter = new AnalyticsExportService(new ExportOptions(MaskDeviceId: true, MaskWaferId: true, DeviceId: "AOI-01"));

        // 启动即做一轮保留期清理：历史是回测资产，但不能无限长（PRD NFR-21）
        try
        {
            new StorageGovernor(_store, new StorageGovernanceOptions(RetentionDays: options.RetentionDays))
                .RunOnce(DateTimeOffset.UtcNow);
        }
        catch (Exception ex)
        {
            _lastError = $"存储治理失败：{ex.Message}";
        }

        if (options.AutoStart)
        {
            Start();
        }
    }

    public string StoreRoot => _store.RootPath;

    public string ExportDirectory => _exportDirectory;

    public string? OptionsFilePath => _optionsProvider.Source;

    /// <summary>启动周期分析（默认 5 分钟一次，参数文件可调）。重复调用无副作用。</summary>
    public void Start() => _scheduler.Start();

    public void Stop() => _scheduler.Stop();

    /// <summary>立即把缓冲落盘（退出前与长稳观测点用；写入失败只计数，不抛）。返回本次刷出的条数。</summary>
    public int Flush() => _sink.Flush();

    /// <summary>立即执行一轮分析（会读库，耗时随窗口大小增长）。返回本轮发布的预警。</summary>
    public IReadOnlyList<AlertEvent> AnalyzeNow()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        try
        {
            var published = _scheduler.RunOnce();
            lock (_gate)
            {
                _lastRunAt = DateTimeOffset.UtcNow;
                var failed = _scheduler.LastRuns.Where(r => r.Failed).Select(r => r.PredictorName).ToList();
                _lastError = failed.Count == 0 ? null : $"预测器异常：{string.Join("、", failed)}";
            }

            return published;
        }
        catch (Exception ex)
        {
            // 分析失败只影响健康页结论，绝不影响采集与 3D
            lock (_gate)
            {
                _lastRunAt = DateTimeOffset.UtcNow;
                _lastError = ex.Message;
            }

            return [];
        }
    }

    /// <summary>
    /// 当前快照：档案来自聚合器（内存），评分是纯计算，**不读库**。
    /// 因此健康页可以 1Hz 刷新而不会随数据量变慢；只有 <see cref="AnalyzeNow"/> 才碰磁盘。
    /// </summary>
    public AnalyticsSnapshot Snapshot()
    {
        var profiles = _aggregator.Snapshot();
        var runs = _scheduler.LastRuns;
        var findings = runs.SelectMany(r => r.Findings).ToList();
        var scores = _scorer.ScoreAll(profiles, findings);

        int partitions;
        long bytes;
        try
        {
            var parts = _store.Partitions();
            partitions = parts.Count;
            bytes = parts.Sum(p => p.Bytes);
        }
        catch (Exception ex)
        {
            partitions = 0;
            bytes = 0;
            _lastError = $"时序库读取失败：{ex.Message}";
        }

        lock (_gate)
        {
            return new AnalyticsSnapshot(
                _sink.Stats,
                partitions,
                bytes,
                _scheduler.IsRunning,
                _lastRunAt,
                runs,
                scores,
                [.. _history],
                _scheduler.PublishedCount,
                _optionsProvider.Current,
                _optionsProvider.LastError,
                _lastError);
        }
    }

    /// <summary>导出健康档案（默认写到 <see cref="ExportDirectory"/>，文件名带时间戳避免覆盖）。</summary>
    public ExportResult ExportHealth(string? filePath = null) =>
        _exporter.ExportHealth(
            Snapshot().Scores,
            filePath ?? NewExportPath("health", "csv"),
            ExportFormat.Csv,
            ExportRole.Engineer);

    /// <summary>导出预警历史。</summary>
    public ExportResult ExportAlerts(string? filePath = null) =>
        _exporter.ExportAlerts(
            Snapshot().Alerts,
            filePath ?? NewExportPath("alerts", "json"),
            ExportFormat.Json,
            ExportRole.Engineer);

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _scheduler.AlertRaised -= OnAlertRaised;
        _scheduler.Dispose();
        _aggregator.Dispose();
        _sink.Dispose(); // 退出前落盘：Dispose 内部会 Flush，未刷的批次不丢
    }

    private void OnAlertRaised(AlertEvent alert)
    {
        lock (_gate)
        {
            _history.Insert(0, alert);
            if (_history.Count > MaxAlertHistory)
            {
                _history.RemoveRange(MaxAlertHistory, _history.Count - MaxAlertHistory);
            }
        }
    }

    private string NewExportPath(string prefix, string extension) =>
        Path.Combine(
            _exportDirectory,
            $"{prefix}-{DateTimeOffset.Now:yyyyMMdd-HHmmss}.{extension}");
}
