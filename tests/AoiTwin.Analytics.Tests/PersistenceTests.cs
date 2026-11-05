using System.IO;
using System.Text;
using AoiTwin.Core.Analytics;
using AoiTwin.Core.Bus;
using AoiTwin.Core.Persistence;
using AoiTwin.Core.Recording;
using Xunit;

namespace AoiTwin.Analytics.Tests;

/// <summary>
/// S1 数据底座验收（TB.0–TB.4）：分区存储、Sink 降级、录制回灌、存储治理、历史回放、健康档案。
/// 全部离线：不依赖任何真实设备，时间戳由测试构造。
/// </summary>
public sealed class PersistenceTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "aoi-m3-" + Guid.NewGuid().ToString("N")[..8]);

    private static readonly DateTimeOffset T0 = new(2026, 10, 1, 8, 0, 0, TimeSpan.Zero);

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

    private FileTimeSeriesStore Store() => new(_root);

    [Fact]
    public void Append_and_query_are_partitioned_by_utc_day()
    {
        var store = Store();
        var day1 = new TimeSeriesRecord(T0, TwinTopic.Motion, RecFileFormat.MessageType.AxisSnapshot, "STAGE", "{\"a\":1}");
        var day2 = new TimeSeriesRecord(T0.AddDays(1), TwinTopic.Motion, RecFileFormat.MessageType.AxisSnapshot, "STAGE", "{\"a\":2}");

        store.Append([day1, day2]);

        Assert.Equal(2, store.Partitions().Count);
        var hits = store.Query(new TimeSeriesQuery(T0.AddHours(-1), T0.AddHours(1))).ToList();
        Assert.Single(hits);
        Assert.Equal(day1.PayloadJson, hits[0].PayloadJson);
    }

    [Fact]
    public void Query_filters_topic_and_part_id()
    {
        var store = Store();
        store.Append(
        [
            TimeSeriesRecord.FromMessage(TwinTopic.Motion, new AxisSnapshot("STAGE", "Z", 1000, 0, T0)),
            TimeSeriesRecord.FromMessage(TwinTopic.Optics, new OpticsEvent("LIGHT_RING", OpticsKind.LightChannel, 1, 90, T0.AddSeconds(1))),
            TimeSeriesRecord.FromMessage(TwinTopic.Motion, new AxisSnapshot("ROBOT", "R", 2000, 0, T0.AddSeconds(2))),
        ]);

        var optics = store.Query(new TimeSeriesQuery(T0.AddMinutes(-1), T0.AddMinutes(5), TwinTopic.Optics)).ToList();
        Assert.Single(optics);

        var stage = store.Query(new TimeSeriesQuery(T0.AddMinutes(-1), T0.AddMinutes(5), partId: "STAGE")).ToList();
        Assert.Single(stage);
        Assert.IsType<AxisSnapshot>(stage[0].ToMessage());
    }

    [Fact]
    public void Corrupt_line_is_skipped_and_does_not_poison_partition()
    {
        var store = Store();
        store.Append([TimeSeriesRecord.FromMessage(TwinTopic.Status, new StatusEvent("TRACK", "WaferPresent", Severity.Info, null, T0))]);

        var path = store.PathFor(DateOnly.FromDateTime(T0.UtcDateTime));
        File.AppendAllText(path, "{ this is not json }\n", new UTF8Encoding(false));
        store.Append([TimeSeriesRecord.FromMessage(TwinTopic.Status, new StatusEvent("TRACK", "WaferPresent", Severity.Info, null, T0.AddSeconds(1)))]);

        var hits = store.Query(new TimeSeriesQuery(T0.AddMinutes(-1), T0.AddMinutes(1))).ToList();
        Assert.Equal(2, hits.Count);
    }

    [Fact]
    public void Estimate_bytes_and_prune_work_by_partition()
    {
        var store = Store();
        store.Append(
        [
            TimeSeriesRecord.FromMessage(TwinTopic.Motion, new AxisSnapshot("STAGE", "Z", 1, 0, T0.AddDays(-40))),
            TimeSeriesRecord.FromMessage(TwinTopic.Motion, new AxisSnapshot("STAGE", "Z", 2, 0, T0)),
        ]);

        Assert.True(store.EstimateBytes(T0.AddDays(-41), T0.AddDays(1)) > 0);

        var removed = store.Prune(DateOnly.FromDateTime(T0.UtcDateTime).AddDays(-30));
        Assert.True(removed > 0);
        Assert.Single(store.Partitions());
    }

    [Fact]
    public void Sink_flushes_buffered_messages_into_store()
    {
        var hub = new RealtimeHub();
        var store = Store();
        using (var sink = new PersistenceSink(store, hub, new PersistenceOptions(_root, FlushInterval: TimeSpan.FromHours(1))))
        {
            hub.Publish(TwinTopic.Motion, new AxisSnapshot("STAGE", "Z", 1500, 0, T0));
            hub.Publish(TwinTopic.Status, new StatusEvent("TRACK", "WaferPresent", Severity.Info, null, T0.AddMilliseconds(10)));
            hub.Publish(TwinTopic.Alert, new AlertEvent("LIGHT_RING", AlertKind.LightDecay, Severity.Warning, 42, "test", [], 5, 0.9, T0));

            Assert.Equal(0, sink.Stats.Written);
            sink.Flush();
            Assert.Equal(3, sink.Stats.Written);
            Assert.True(sink.Stats.Healthy);
        }

        Assert.Equal(3, store.Query(new TimeSeriesQuery(T0.AddMinutes(-1), T0.AddMinutes(1))).Count());
    }

    [Fact]
    public void Sink_swallows_store_failures_and_reports_them()
    {
        var hub = new RealtimeHub();
        using var sink = new PersistenceSink(new ThrowingStore(), hub, new PersistenceOptions(_root, FlushInterval: TimeSpan.FromHours(1)));

        // 发布不得抛出（实时链路优先）
        hub.Publish(TwinTopic.Motion, new AxisSnapshot("STAGE", "Z", 1, 0, T0));
        hub.Publish(TwinTopic.Motion, new AxisSnapshot("STAGE", "Z", 2, 0, T0.AddMilliseconds(10)));

        var written = sink.Flush(); // 也不得抛出

        Assert.Equal(0, written);
        Assert.True(sink.Stats.FailedBatches > 0);
        Assert.False(sink.Stats.Healthy);
        Assert.NotNull(sink.Stats.LastError);
    }

    [Fact]
    public void Sink_drops_oldest_when_buffer_exceeds_limit()
    {
        var hub = new RealtimeHub();
        using var sink = new PersistenceSink(
            new ThrowingStore(),
            hub,
            new PersistenceOptions(_root, FlushInterval: TimeSpan.FromHours(1), MaxBufferRecords: 2));

        for (var i = 0; i < 5; i++)
        {
            hub.Publish(TwinTopic.Motion, new AxisSnapshot("STAGE", "Z", i, 0, T0.AddMilliseconds(i)));
        }

        Assert.True(sink.Stats.DroppedByBuffer >= 2);
        Assert.True(sink.Stats.Pending <= 2);
    }

    [Fact]
    public void Rec_importer_round_trips_recording_into_store()
    {
        var dir = Path.Combine(_root, "rec");
        Directory.CreateDirectory(dir);
        string file;

        using (var writer = new RecordingWriter(new RecordingOptions
               {
                   Directory = dir,
                   DeviceId = "AOI-01",
                   DiskUsedRatioProvider = () => 0.1, // 测试机磁盘水位不可控，显式置低
               }))
        {
            writer.Write(TwinTopic.Motion, new AxisSnapshot("STAGE", "Z", 100, 0, T0));
            writer.Write(TwinTopic.Optics, new OpticsEvent("LIGHT_RING", OpticsKind.LightBrightness, 0, 88, T0.AddSeconds(1)));
            writer.Write(TwinTopic.Result, new WaferResult("ST01-0001", new WaferMapSnapshot(2, 2, [0, 1, 2, 3]), null, TimeSpan.FromSeconds(6), T0.AddSeconds(2)));
            file = writer.CurrentFilePath ?? throw new InvalidOperationException("录制文件未创建");
        }

        var store = Store();
        var result = RecImporter.Import(file, store);

        Assert.Equal(3, result.FramesScanned);
        Assert.Equal(3, result.Imported);
        Assert.Empty(result.Errors);
        Assert.Equal(3, store.Query(new TimeSeriesQuery(T0.AddMinutes(-1), T0.AddMinutes(1))).Count());
    }

    [Fact]
    public void Governor_prunes_expired_and_reports_watermark()
    {
        var store = Store();
        store.Append(
        [
            TimeSeriesRecord.FromMessage(TwinTopic.Motion, new AxisSnapshot("STAGE", "Z", 1, 0, T0.AddDays(-45))),
            TimeSeriesRecord.FromMessage(TwinTopic.Motion, new AxisSnapshot("STAGE", "Z", 2, 0, T0)),
        ]);

        var governor = new StorageGovernor(store, new StorageGovernanceOptions(RetentionDays: 30), _ => long.MaxValue);
        var report = governor.RunOnce(T0);

        Assert.True(report.Healthy);
        Assert.True(report.DeletedBytes > 0);
        Assert.Single(store.Partitions());
        Assert.NotEmpty(report.Actions);
    }

    [Fact]
    public void Governor_enters_emergency_cleanup_when_disk_low()
    {
        var store = Store();
        store.Append(
        [
            TimeSeriesRecord.FromMessage(TwinTopic.Motion, new AxisSnapshot("STAGE", "Z", 1, 0, T0.AddDays(-5))),
            TimeSeriesRecord.FromMessage(TwinTopic.Motion, new AxisSnapshot("STAGE", "Z", 2, 0, T0)),
        ]);

        var governor = new StorageGovernor(
            store,
            new StorageGovernanceOptions(RetentionDays: 30, MinFreeBytes: 1024, EmergencyKeepDays: 1),
            _ => 0);

        var report = governor.RunOnce(T0);

        Assert.False(report.Healthy);
        Assert.Single(store.Partitions());
        Assert.Contains(report.Actions, a => a.Contains("应急清理", StringComparison.Ordinal));
    }

    [Fact]
    public void History_replay_publishes_records_in_order_with_remapped_timestamps()
    {
        var store = Store();
        store.Append(
        [
            TimeSeriesRecord.FromMessage(TwinTopic.Motion, new AxisSnapshot("STAGE", "Z", 1, 0, T0)),
            TimeSeriesRecord.FromMessage(TwinTopic.Motion, new AxisSnapshot("STAGE", "Z", 2, 0, T0.AddSeconds(1))),
            TimeSeriesRecord.FromMessage(TwinTopic.Motion, new AxisSnapshot("STAGE", "Z", 3, 0, T0.AddSeconds(2))),
        ]);

        var hub = new RealtimeHub();
        var received = new List<AxisSnapshot>();
        using var subscription = hub.Subscribe<AxisSnapshot>(TwinTopic.Motion, received.Add);
        using var engine = new TimeSeriesReplayEngine(store, hub);

        var frames = engine.Load(new TimeSeriesQuery(T0.AddMinutes(-1), T0.AddMinutes(1)));
        Assert.Equal(3, frames);

        while (engine.Step())
        {
        }

        Assert.Equal(3, received.Count);
        Assert.Equal([1.0, 2.0, 3.0], received.Select(r => r.Position));
        Assert.True(received[1].Timestamp >= received[0].Timestamp);
        Assert.True(received[2].Timestamp >= received[1].Timestamp);
        Assert.Equal(HistoryReplayState.Completed, engine.State);
    }

    [Fact]
    public void Part_health_aggregates_travel_alarms_light_hours_and_tact()
    {
        var hub = new RealtimeHub();
        using var aggregator = new PartHealthAggregator(hub);

        for (var i = 0; i < 4; i++)
        {
            hub.Publish(TwinTopic.Motion, new AxisSnapshot("STAGE", "Z", 1000 + (i * 1000), 0, T0.AddSeconds(i)));
        }

        hub.Publish(TwinTopic.Status, new StatusEvent("STAGE", "ALM-01", Severity.Alarm, null, T0.AddSeconds(5)));
        hub.Publish(TwinTopic.Status, new StatusEvent("STAGE", "WRN-01", Severity.Warning, null, T0.AddSeconds(6)));

        // 两小时内持续点灯（10s 累计上限内逐条累计）
        for (var i = 0; i < 60; i++)
        {
            hub.Publish(TwinTopic.Optics, new OpticsEvent("LIGHT_RING", OpticsKind.LightBrightness, 0, 80, T0.AddSeconds(i * 10)));
        }

        var map = new WaferMapSnapshot(1, 1, [1]);
        hub.Publish(TwinTopic.Result, new WaferResult("ST01-1", map, null, TimeSpan.FromSeconds(6), T0.AddMinutes(10)));
        hub.Publish(TwinTopic.Result, new WaferResult("ST01-2", map, null, TimeSpan.FromSeconds(8), T0.AddMinutes(11)));

        var stage = aggregator.Get("STAGE");
        Assert.NotNull(stage);
        Assert.Equal(4, stage!.AxisMessageCount);
        Assert.Equal(3.0, stage.TravelMm, 3);
        Assert.Equal(1, stage.AlarmCount);
        Assert.Equal(1, stage.WarningCount);

        var light = aggregator.Get("LIGHT_RING");
        Assert.NotNull(light);
        Assert.True(light!.LightOnHours > 0.1);

        var wafer = aggregator.Get("WAFER_CURRENT");
        Assert.NotNull(wafer);
        Assert.Equal(2, wafer!.WaferCount);
        Assert.Equal(7.0, wafer.MeanTactSec, 3);
        Assert.Equal(8.0, wafer.LastTactSec, 3);
    }
}

/// <summary>写入必失败的存储（验证 Sink 的降级与重试路径）。</summary>
internal sealed class ThrowingStore : ITimeSeriesStore
{
    public string RootPath => Path.Combine(Path.GetTempPath(), "aoi-throwing");

    public void Append(IReadOnlyList<TimeSeriesRecord> records) => throw new IOException("disk full (simulated)");

    public IEnumerable<TimeSeriesRecord> Query(TimeSeriesQuery query) => [];

    public IReadOnlyList<TimeSeriesPartition> Partitions() => [];

    public long EstimateBytes(DateTimeOffset from, DateTimeOffset to) => 0;

    public long Prune(DateOnly before) => 0;
}
