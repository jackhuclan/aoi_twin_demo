using System.IO;
using AoiTwin.Core.Analytics;
using AoiTwin.Core.Bus;
using AoiTwin.Core.Persistence;
using Xunit;

namespace AoiTwin.Analytics.Tests;

/// <summary>
/// TB.18 长稳的**离线加速**验证：把 30 天数据压缩到秒级跑完。
/// 它证明的不是「真机 30 天没问题」（那必须现场跑），而是：
/// 分区滚动/治理/写入/分析在长时间跨度上不出现结构性问题（分区爆炸、内存线性增长、分析失败）。
/// </summary>
public sealed class SoakSimulationTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "aoi-m3-soak-" + Guid.NewGuid().ToString("N")[..8]);
    private static readonly DateTimeOffset Start = new(2026, 10, 1, 0, 0, 0, TimeSpan.Zero);

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

    [Fact]
    public void Thirty_day_accumulation_keeps_partitions_bounded_and_analysis_healthy()
    {
        var store = new FileTimeSeriesStore(_root);
        var hub = new RealtimeHub();
        using var sink = new PersistenceSink(store, hub, new PersistenceOptions(_root, BatchSize: 10_000, FlushInterval: TimeSpan.FromHours(1)));
        using var aggregator = new PartHealthAggregator(hub);
        var governor = new StorageGovernor(store, new StorageGovernanceOptions(RetentionDays: 30, MinFreeBytes: 1024), _ => long.MaxValue);

        var registry = new PredictorRegistry();
        registry.RegisterRange([new LightDecayPredictor(), new AxisWearPredictor(), new TactTrendAnalyzer()]);

        var map = new WaferMapSnapshot(1, 1, [1]);
        var baselineMemory = GC.GetTotalMemory(forceFullCollection: true);
        DateTimeOffset last = Start;

        for (var day = 0; day < 30; day++)
        {
            for (var hour = 0; hour < 24; hour++)
            {
                var ts = Start.AddDays(day).AddHours(hour);
                last = ts;

                // 光源缓慢衰减（30 天从 100 降到 82）
                hub.Publish(TwinTopic.Optics, new OpticsEvent("LIGHT_RING", OpticsKind.LightBrightness, 0, 100 - (day * 0.6), ts));

                // 轴位置小幅抖动（保持段）
                hub.Publish(TwinTopic.Motion, new AxisSnapshot("STAGE", "Z", 5000 + ((hour % 3) - 1), 0, ts));

                // 节拍缓慢劣化（6.0s → 6.6s）
                hub.Publish(TwinTopic.Result, new WaferResult($"ST01-{day:D2}{hour:D2}", map, null, TimeSpan.FromSeconds(6 + (day * 0.02)), ts));
            }

            sink.Flush();
            governor.RunOnce(last.AddHours(1));
        }

        Assert.True(sink.Stats.Healthy);
        Assert.Equal(30 * 24 * 3, sink.Stats.Written);
        Assert.InRange(store.Partitions().Count, 28, 31);

        var context = new AnalyticsContext(last, aggregator.Snapshot(), new AnalyticsOptions(), store, windowOverride: TimeSpan.FromDays(30));
        var runs = registry.RunAll(context);

        Assert.All(runs, run => Assert.False(run.Failed, run.Error));

        // 30 天衰减足以让光衰模型给出预测（端到端：写盘 → 查库 → 拟合）
        var lightFinding = runs
            .SelectMany(r => r.Findings)
            .FirstOrDefault(f => f.Kind == AlertKind.LightDecay);
        Assert.NotNull(lightFinding);

        var growth = GC.GetTotalMemory(forceFullCollection: true) - baselineMemory;
        Assert.True(growth < 64L * 1024 * 1024, $"内存增长 {growth / 1024.0 / 1024.0:F1} MB 超出预期");
    }
}
