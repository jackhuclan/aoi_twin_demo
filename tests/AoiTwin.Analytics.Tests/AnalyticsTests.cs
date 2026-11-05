using System.IO;
using AoiTwin.Core.Analytics;
using AoiTwin.Core.Bus;
using AoiTwin.Core.Persistence;
using Xunit;

namespace AoiTwin.Analytics.Tests;

/// <summary>
/// S2 分析层验收（TB.5–TB.10）：拟合、评分、三个预测模型、调度去重/升级/恢复、插件签名。
/// 回测数据全部由测试按已知规律构造，因此「提前量」可解析验证，不依赖真机。
/// </summary>
public sealed class AnalyticsTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "aoi-m3-an-" + Guid.NewGuid().ToString("N")[..8]);
    private static readonly DateTimeOffset Now = new(2026, 11, 5, 9, 0, 0, TimeSpan.Zero);

    public void Dispose()
    {
        try
        {
            if (Directory.Exists(_root))
            {
                Directory.Delete(_root, recursive: true);
            }
        }
        catch (IOException)
        {
        }
    }

    private (FileTimeSeriesStore Store, AnalyticsContext Context) NewContext(AnalyticsOptions options, IReadOnlyList<PartHealthProfile>? profiles = null)
    {
        var store = new FileTimeSeriesStore(_root);
        var context = new AnalyticsContext(Now, profiles ?? [], options, store, windowOverride: options.Window);
        return (store, context);
    }

    [Fact]
    public void Linear_trend_fits_exact_line_and_reports_unit_r2()
    {
        var points = Enumerable.Range(0, 11).Select(i => ((double)i, 10.0 + (2.0 * i))).ToList();
        var trend = LinearTrend.Fit(points);

        Assert.True(trend.IsValid);
        Assert.Equal(2.0, trend.Slope, 6);
        Assert.Equal(10.0, trend.Intercept, 6);
        Assert.Equal(1.0, trend.RSquared, 6);
        Assert.Equal(20.0, trend.DaysToReach(50), 6);
        Assert.Equal(11, trend.Count);
    }

    [Fact]
    public void Light_decay_predicts_remaining_days_until_warning_line()
    {
        var (store, context) = NewContext(new AnalyticsOptions { LightWarningHorizonDays = 7, LightCriticalHorizonDays = 3, LightMinSamples = 10 });
        var start = Now.AddDays(-30);
        var records = new List<TimeSeriesRecord>();
        for (var day = 0; day <= 30; day++)
        {
            // 每天降 0.6 → 30 天后 82；预警线 80（额定 100 的 80%）将在第 33.3 天到达 → 约 3.3 天后
            var value = 100 - (0.6 * day);
            records.Add(TimeSeriesRecord.FromMessage(
                TwinTopic.Optics,
                new OpticsEvent("LIGHT_RING", OpticsKind.LightBrightness, 0, value, start.AddDays(day))));
        }

        store.Append(records);

        var findings = new LightDecayPredictor().Analyze(context);

        var finding = Assert.Single(findings);
        Assert.Equal(AlertKind.LightDecay, finding.Kind);
        Assert.Equal(Severity.Warning, finding.Severity);
        Assert.InRange(finding.HorizonDays ?? 0, 2, 5);
        Assert.True(finding.Confidence > 0.99);
        Assert.Contains(finding.Factors, f => f.Name == "衰减速率");
    }

    [Fact]
    public void Light_decay_gives_seven_day_lead_time_on_backtest_window()
    {
        // 回测口径（PRD §7-2「提前量 ≥ 7 天」）：以 0.4/天 衰减，预警线 80 在第 50 天到达；
        // 取 now = 第 43 天（剩余 7 天）时，模型必须已经报出 Warning —— 即首次预警提前 7 天。
        // 额定值必须显式配置：窗口首样本会随滑窗漂移，不能当额定基准
        var (store, context) = NewContext(new AnalyticsOptions { LightRatedValue = 100, LightWarningHorizonDays = 7, LightMinSamples = 10 });
        var start = Now.AddDays(-43);
        store.Append(Enumerable.Range(0, 44).Select(day => TimeSeriesRecord.FromMessage(
            TwinTopic.Optics,
            new OpticsEvent("LIGHT_RING", OpticsKind.LightBrightness, 0, 100 - (0.4 * day), start.AddDays(day)))).ToList());

        var finding = Assert.Single(new LightDecayPredictor().Analyze(context));

        Assert.Equal(Severity.Warning, finding.Severity);
        Assert.InRange(finding.HorizonDays ?? 0, 6, 7);
        Assert.Contains("天后跌至预警线", finding.Summary, StringComparison.Ordinal);
    }

    [Fact]
    public void Light_decay_is_silent_without_degradation_or_with_too_few_samples()
    {
        var (flatStore, flatContext) = NewContext(new AnalyticsOptions { LightMinSamples = 10 });
        var start = Now.AddDays(-10);
        flatStore.Append(Enumerable.Range(0, 11).Select(day => TimeSeriesRecord.FromMessage(
            TwinTopic.Optics,
            new OpticsEvent("LIGHT_RING", OpticsKind.LightBrightness, 0, 95, start.AddDays(day)))).ToList());

        Assert.Empty(new LightDecayPredictor().Analyze(flatContext));

        var (fewStore, fewContext) = NewContext(new AnalyticsOptions { LightMinSamples = 10 });
        fewStore.Append(Enumerable.Range(0, 3).Select(day => TimeSeriesRecord.FromMessage(
            TwinTopic.Optics,
            new OpticsEvent("LIGHT_RING", OpticsKind.LightBrightness, 0, 60, start.AddDays(day)))).ToList());

        Assert.Empty(new LightDecayPredictor().Analyze(fewContext));
    }

    [Fact]
    public void Axis_wear_detects_growing_hold_segment_scatter()
    {
        var (store, context) = NewContext(new AnalyticsOptions { AxisMinSamples = 20, AxisWearSigmaThreshold = 2.0, AxisDriftUmPerDay = 1.0 });
        var random = new Random(20261105);
        var records = new List<TimeSeriesRecord>();
        var timestamp = Now.AddDays(-2);

        for (var segment = 0; segment < 20; segment++)
        {
            var sigma = segment < 10 ? 1.0 : 12.0; // 后半段重复定位变差
            for (var i = 0; i < 10; i++)
            {
                var jitter = (random.NextDouble() - 0.5) * 2 * sigma;
                records.Add(TimeSeriesRecord.FromMessage(
                    TwinTopic.Motion,
                    new AxisSnapshot("STAGE", "Z", 5000 + jitter, 0, timestamp)));
                timestamp = timestamp.AddSeconds(1);
            }

            // 两条移动样本把保持段断开
            records.Add(TimeSeriesRecord.FromMessage(TwinTopic.Motion, new AxisSnapshot("STAGE", "Z", 9000, 1000, timestamp)));
            timestamp = timestamp.AddSeconds(1);
        }

        store.Append(records);
        var findings = new AxisWearPredictor().Analyze(context);

        var finding = Assert.Single(findings);
        Assert.Equal(AlertKind.AxisWear, finding.Kind);
        Assert.True(finding.Severity is Severity.Warning or Severity.Alarm);
        Assert.Contains(finding.Factors, f => f.Name == "z 分数");
    }

    [Fact]
    public void Axis_wear_is_silent_on_stable_scatter()
    {
        var (store, context) = NewContext(new AnalyticsOptions { AxisMinSamples = 20, AxisWearSigmaThreshold = 3.0, AxisDriftUmPerDay = 5.0 });
        var random = new Random(7);
        var records = new List<TimeSeriesRecord>();
        var timestamp = Now.AddDays(-2);

        for (var segment = 0; segment < 20; segment++)
        {
            for (var i = 0; i < 10; i++)
            {
                var jitter = (random.NextDouble() - 0.5) * 2;
                records.Add(TimeSeriesRecord.FromMessage(
                    TwinTopic.Motion,
                    new AxisSnapshot("STAGE", "Z", 5000 + jitter, 0, timestamp)));
                timestamp = timestamp.AddSeconds(1);
            }

            records.Add(TimeSeriesRecord.FromMessage(TwinTopic.Motion, new AxisSnapshot("STAGE", "Z", 9500, 1000, timestamp)));
            timestamp = timestamp.AddSeconds(1);
        }

        store.Append(records);
        Assert.Empty(new AxisWearPredictor().Analyze(context));
    }

    [Fact]
    public void Tact_trend_flags_degradation_and_reports_bottleneck()
    {
        var (store, context) = NewContext(new AnalyticsOptions { TactMinSamples = 20, TactDegradationPercent = 5 });
        var start = Now.AddDays(-2);
        var records = new List<TimeSeriesRecord>();
        var map = new WaferMapSnapshot(1, 1, [1]);

        for (var i = 0; i < 25; i++)
        {
            var tact = TimeSpan.FromSeconds(6.0 + (i * 0.05));
            records.Add(TimeSeriesRecord.FromMessage(
                TwinTopic.Result,
                new WaferResult($"ST01-{i:D4}", map, null, tact, start.AddMinutes(i * 3))));
        }

        // 瓶颈数据：ST02 平均 9s、ST03 平均 4s
        for (var i = 0; i < 25; i++)
        {
            records.Add(TimeSeriesRecord.FromMessage(
                TwinTopic.Result,
                new WaferResult($"ST02-{i:D4}", map, null, TimeSpan.FromSeconds(9), start.AddMinutes(i * 3))));
            records.Add(TimeSeriesRecord.FromMessage(
                TwinTopic.Result,
                new WaferResult($"ST03-{i:D4}", map, null, TimeSpan.FromSeconds(4), start.AddMinutes(i * 3))));
        }

        store.Append(records);

        var findings = new TactTrendAnalyzer().Analyze(context);
        var st01 = Assert.Single(findings, f => f.PartId == "ST01");
        Assert.Equal(AlertKind.TactDegradation, st01.Kind);
        Assert.Equal(Severity.Alarm, st01.Severity);

        var bottleneck = TactTrendAnalyzer.Bottlenecks(context, top: 1);
        Assert.Single(bottleneck);
        Assert.Equal("ST02", bottleneck[0].Station);
    }

    [Fact]
    public void Health_scorer_uses_weighted_penalties_and_grades()
    {
        var scorer = new HealthScorer();
        var profile = new PartHealthProfile("STAGE", AxisMessageCount: 100, TravelMm: 2000, AlarmCount: 2, WarningCount: 3, LightOnHours: 100, WaferCount: 10, MeanTactSec: 6, LastTactSec: 6, LastSeen: Now);

        var score = scorer.Score(profile);

        Assert.True(score.Score < 50);
        Assert.Equal(HealthGrade.Critical, score.Grade);
        Assert.Contains(score.Factors, f => f.Name == "报警" && f.Value == 50);
    }

    [Fact]
    public void Health_scorer_skips_parts_without_observations()
    {
        var scorer = new HealthScorer();
        var scores = scorer.ScoreAll([PartHealthProfile.Empty("CAMERA")]);
        Assert.Empty(scores);
    }

    [Fact]
    public void Scheduler_publishes_dedupes_escalates_and_recovers()
    {
        var hub = new RealtimeHub();
        var received = new List<AlertEvent>();
        using var subscription = hub.Subscribe<AlertEvent>(TwinTopic.Alert, received.Add);
        var registry = new PredictorRegistry();
        var predictor = new MutablePredictor();
        registry.Register(predictor);
        using var scheduler = new AlertScheduler(hub, registry, () => new AnalyticsContext(Now, [], new AnalyticsOptions()));

        predictor.Finding = new AlertFinding("LIGHT_RING", AlertKind.LightDecay, Severity.Warning, 40, "预警", []);
        scheduler.RunOnce();
        Assert.Single(received);
        // 能收到即证明走通了 TwinTopic.Alert（订阅侧按 Topic 过滤）；此处校验载荷语义
        Assert.Equal("LIGHT_RING", received[0].PartId);
        Assert.Equal(Severity.Warning, received[0].Severity);
        Assert.Equal("LIGHT_RING|LightDecay", received[0].DedupeKey);
        Assert.Equal(Now, received[0].Timestamp);

        // 同级别、抑制窗口内：不重复发布
        scheduler.RunOnce();
        Assert.Single(received);

        // 升级：立即发布
        predictor.Finding = new AlertFinding("LIGHT_RING", AlertKind.LightDecay, Severity.Alarm, 90, "严重", []);
        scheduler.RunOnce();
        Assert.Equal(2, received.Count);
        Assert.Equal(Severity.Alarm, received[^1].Severity);

        // 消失：发 Info 恢复，让 3D 退色
        predictor.Finding = null;
        scheduler.RunOnce();
        Assert.Equal(3, received.Count);
        Assert.Equal(Severity.Info, received[^1].Severity);
        Assert.Equal("预警已解除", received[^1].Summary);
    }

    [Fact]
    public void Predictor_registry_isolates_failing_plugin()
    {
        var registry = new PredictorRegistry();
        registry.Register(new MutablePredictor { Name = "ok" });
        registry.Register(new ThrowingPredictor());

        var runs = registry.RunAll(new AnalyticsContext(Now, [], new AnalyticsOptions()));

        Assert.Equal(2, runs.Count);
        Assert.Contains(runs, r => r.PredictorName == "ok" && !r.Failed);
        Assert.Contains(runs, r => r.Failed && r.Error!.Contains("boom", StringComparison.Ordinal));
    }

    [Fact]
    public void Plugin_loader_rejects_unsigned_assemblies_by_default()
    {
        var dir = Path.Combine(_root, "plugins");
        Directory.CreateDirectory(dir);
        var self = typeof(AnalyticsTests).Assembly.Location;
        File.Copy(self, Path.Combine(dir, Path.GetFileName(self)), overwrite: true);

        var registry = new PredictorRegistry();
        var report = PluginLoader.LoadPredictors(registry, dir, PluginLoadOptions.DenyAll);

        Assert.Equal(0, report.Loaded);
        Assert.True(report.Rejected >= 1);
        Assert.Contains(report.Messages, m => m.Contains("签名不受信任", StringComparison.Ordinal));
        Assert.Equal(0, registry.Count);

        var missing = PluginLoader.LoadPredictors(registry, Path.Combine(_root, "nope"), PluginLoadOptions.DenyAll);
        Assert.Contains(missing.Messages, m => m.Contains("不存在", StringComparison.Ordinal));
    }

    private sealed class MutablePredictor : IAlertPredictor
    {
        public string Name { get; init; } = "mutable";

        public AlertFinding? Finding { get; set; }

        public IReadOnlyList<AlertFinding> Analyze(IAnalyticsContext context)
            => Finding is null ? [] : [Finding];
    }

    private sealed class ThrowingPredictor : IAlertPredictor
    {
        public string Name => "boom";

        public IReadOnlyList<AlertFinding> Analyze(IAnalyticsContext context) => throw new InvalidOperationException("boom");
    }
}
