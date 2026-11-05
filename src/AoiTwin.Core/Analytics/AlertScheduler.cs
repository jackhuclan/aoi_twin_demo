using AoiTwin.Core.Bus;

namespace AoiTwin.Core.Analytics;

/// <summary>
/// 预警调度器（TB.5/TB.10）：周期（默认 5min）+ 事件触发两种方式执行全部预测器，
/// 把 <see cref="AlertFinding"/> 转成 <see cref="AlertEvent"/> 发布到 <see cref="TwinTopic.Alert"/>。
///
/// 去重与升级语义（可测，且直接决定现场是否被 3D 提示打扰）：
/// - 同 <see cref="AlertEvent.DedupeKey"/> 的预警在 <see cref="AnalyticsOptions.RepeatSuppression"/> 内只发一次；
/// - 严重度上升（Warning→Critical）**立即**发，不等抑制窗口；
/// - 预警消失时发一条 <see cref="Severity.Info"/>「已恢复」，让 3D 状态能自动退色（不会永远红着）；
/// - 调度器只做「判定与发布」，不直接操作渲染对象——渲染层订阅 Alert Topic，这与 M2 的
///   「分析不碰渲染」边界一致。
/// </summary>
public sealed class AlertScheduler : IDisposable
{
    private readonly IRealtimeHub _hub;
    private readonly PredictorRegistry _registry;
    private readonly Func<IAnalyticsContext> _contextFactory;
    private readonly AnalyticsOptions _options;
    private readonly object _gate = new();
    private readonly Dictionary<string, State> _state = new(StringComparer.Ordinal);

    private Timer? _timer;
    private bool _disposed;
    private IReadOnlyList<AlertEvent> _lastRaised = [];
    private IReadOnlyList<PredictorRun> _lastRuns = [];
    private long _published;
    private int _running;

    public AlertScheduler(
        IRealtimeHub hub,
        PredictorRegistry registry,
        Func<IAnalyticsContext> contextFactory,
        AnalyticsOptions? options = null)
    {
        _hub = hub ?? throw new ArgumentNullException(nameof(hub));
        _registry = registry ?? throw new ArgumentNullException(nameof(registry));
        _contextFactory = contextFactory ?? throw new ArgumentNullException(nameof(contextFactory));
        _options = options ?? new AnalyticsOptions();
    }

    /// <summary>最近一次运行发布的预警（健康页/测试读取）。</summary>
    public IReadOnlyList<AlertEvent> LastRaised
    {
        get { lock (_gate) return _lastRaised; }
    }

    /// <summary>最近一次各预测器的执行结果（含失败原因）。</summary>
    public IReadOnlyList<PredictorRun> LastRuns
    {
        get { lock (_gate) return _lastRuns; }
    }

    public long PublishedCount => Interlocked.Read(ref _published);

    /// <summary>是否正在周期运行。</summary>
    public bool IsRunning
    {
        get { lock (_gate) return _timer is not null; }
    }

    public event Action<AlertEvent>? AlertRaised;

    /// <summary>启动周期调度（立即跑一次，不等第一个周期）。</summary>
    public void Start()
    {
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (_timer is not null)
            {
                return;
            }

            _timer = new Timer(_ => SafeRun(), null, TimeSpan.Zero, _options.SchedulerPeriod);
        }
    }

    public void Stop()
    {
        Timer? timer;
        lock (_gate)
        {
            timer = _timer;
            _timer = null;
        }

        timer?.Dispose();
    }

    /// <summary>事件触发（如收到 Alarm 状态、班次切换）：把下一次运行提前到现在，重复调用被合并。</summary>
    public void Trigger() => _timer?.Change(TimeSpan.Zero, _options.SchedulerPeriod);

    /// <summary>
    /// 同步执行一次分析与发布，返回本次发布的预警（含恢复事件）。
    /// 重入保护：上一轮未结束时直接返回空，避免周期与事件触发叠加造成重复发布。
    /// </summary>
    public IReadOnlyList<AlertEvent> RunOnce()
    {
        if (Interlocked.Exchange(ref _running, 1) == 1)
        {
            return [];
        }

        try
        {
            var context = _contextFactory();
            var runs = _registry.RunAll(context);
            var findings = runs.SelectMany(r => r.Findings).ToList();
            var now = context.Now;
            var published = new List<AlertEvent>();
            var seen = new HashSet<string>(StringComparer.Ordinal);

            foreach (var finding in findings)
            {
                var key = $"{finding.PartId}|{finding.Kind}";
                seen.Add(key);

                State? previous;
                lock (_gate)
                {
                    _state.TryGetValue(key, out previous);
                }

                var shouldPublish = previous is null
                    || Rank(finding.Severity) > Rank(previous.Severity)
                    || Rank(finding.Severity) < Rank(previous.Severity)
                    || now - previous.PublishedAt >= _options.RepeatSuppression;

                if (!shouldPublish)
                {
                    continue;
                }

                var alert = new AlertEvent(
                    finding.PartId,
                    finding.Kind,
                    finding.Severity,
                    finding.Score,
                    finding.Summary,
                    finding.Factors,
                    finding.HorizonDays,
                    finding.Confidence,
                    now);

                Publish(alert, published);
                lock (_gate)
                {
                    _state[key] = new State(finding.Severity, now);
                }
            }

            // 恢复：上一轮有、这一轮没有 → 发 Info，让 3D 状态退色
            List<string> recovered;
            lock (_gate)
            {
                recovered = _state.Keys.Where(k => !seen.Contains(k)).ToList();
                foreach (var key in recovered)
                {
                    _state.Remove(key);
                }
            }

            foreach (var key in recovered)
            {
                var parts = key.Split('|');
                var alert = new AlertEvent(
                    parts[0],
                    Enum.TryParse<AlertKind>(parts[1], out var kind) ? kind : AlertKind.Custom,
                    Severity.Info,
                    0,
                    "预警已解除",
                    [],
                    null,
                    null,
                    now);
                Publish(alert, published);
            }

            lock (_gate)
            {
                _lastRaised = published;
                _lastRuns = runs;
            }

            return published;
        }
        finally
        {
            Volatile.Write(ref _running, 0);
        }
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        Stop();
    }

    private void Publish(AlertEvent alert, List<AlertEvent> sink)
    {
        try
        {
            _hub.Publish(TwinTopic.Alert, alert);
            Interlocked.Increment(ref _published);
            sink.Add(alert);
            AlertRaised?.Invoke(alert);
        }
        catch (Exception)
        {
            // 订阅方异常由 Hub 隔离；这里再兜一层，保证调度循环不因单个预警中断
        }
    }

    private void SafeRun()
    {
        try
        {
            RunOnce();
        }
        catch (Exception)
        {
            // 分析层是 Hub 的普通订阅方，任何异常都不得影响实时链路（NFR-22）
        }
    }

    private static int Rank(Severity severity) => severity switch
    {
        Severity.Alarm => 2,
        Severity.Warning => 1,
        _ => 0,
    };

    private sealed record State(Severity Severity, DateTimeOffset PublishedAt);
}
