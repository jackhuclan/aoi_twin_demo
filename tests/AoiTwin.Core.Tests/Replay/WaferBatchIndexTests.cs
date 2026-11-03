using AoiTwin.Core.Bus;
using AoiTwin.Core.Replay;
using AoiTwin.Core.Recording;
using Xunit;

namespace AoiTwin.Core.Tests.Replay;

/// <summary>
/// T3.2 批次索引与回放会话测试。
/// 核心验收（计划 §4 T3.2）：随机选 10 个批次，跳转后首条消息即为该批次首帧。
/// </summary>
public class WaferBatchIndexTests : IDisposable
{
    private static readonly DateTimeOffset Base = new(2026, 11, 3, 8, 0, 0, TimeSpan.Zero);

    private readonly string _dir;

    public WaferBatchIndexTests()
    {
        _dir = Path.Combine(Path.GetTempPath(), $"batch-{Guid.NewGuid():N}");
        Directory.CreateDirectory(_dir);
    }

    public void Dispose()
    {
        try
        {
            Directory.Delete(_dir, recursive: true);
        }
        catch (IOException)
        {
        }
    }

    /// <summary>每片晶圆：3 帧轴快照 + 1 帧晶圆结果（片间相隔 1s），返回文件路径。</summary>
    private string BuildRecording(int batchCount, int defectsPerWafer = 2, int axisPerBatch = 3)
    {
        using var writer = new RecordingWriter(new RecordingOptions
        {
            Directory = _dir,
            DeviceId = "AOI-01",
            ShiftId = "S1",
            DiskUsedRatioProvider = () => 0.1,
        });

        for (var b = 0; b < batchCount; b++)
        {
            for (var k = 0; k < axisPerBatch; k++)
            {
                writer.Write(TwinTopic.Motion, new AxisSnapshot(
                    "ROBOT", "R", b * 100 + k, 0.5, Base.AddMilliseconds((b * 1000) + (k * 100))));
            }

            var defects = Enumerable.Range(0, defectsPerWafer)
                .Select(i => new DefectRecord(i, i * 1.5, i * 2.5, 3.0 + i, "PARTICLE", 128))
                .ToList();
            writer.Write(TwinTopic.Result, new WaferResult(
                $"WAFER-{b + 1:D3}",
                new WaferMapSnapshot(2, 2, [255, 0, 128, 64]),
                defects,
                TimeSpan.FromSeconds(8.4),
                Base.AddMilliseconds((b * 1000) + 500)));
        }

        return Assert.Single(Directory.GetFiles(_dir, "*.aotrec"));
    }

    [Fact]
    public void Build_extracts_batches_in_order_with_frame_counts()
    {
        var file = BuildRecording(3);
        var index = WaferBatchIndex.Build(file);

        Assert.Equal(3, index.Count);
        Assert.Equal(new[] { "WAFER-001", "WAFER-002", "WAFER-003" }, index.Batches.Select(b => b.WaferId));
        Assert.Equal(new[] { 1, 2, 3 }, index.Batches.Select(b => b.Ordinal));
        Assert.All(index.Batches, b => Assert.Equal(2, b.DefectCount));
        Assert.All(index.Batches, b => Assert.Equal(TimeSpan.FromSeconds(8.4), b.Tact));

        // 每批 4 帧（3 轴 + 1 结果），帧数覆盖全文件且互不重叠
        Assert.Equal(12, index.Batches.Sum(b => b.FrameCount));
        Assert.Equal(0, index.Batches[0].FirstFrameIndex);
        Assert.Equal(4, index.Batches[1].FirstFrameIndex);
        Assert.Equal(TimeSpan.FromSeconds(1), index.Batches[1].StartElapsed);
        Assert.Equal(index.Batches[1].StartElapsed, index.Batches[0].EndElapsed);
    }

    [Fact]
    public void BatchAt_maps_frame_to_owning_batch()
    {
        var file = BuildRecording(3);
        var index = WaferBatchIndex.Build(file);

        Assert.Null(index.BatchAt(-1));
        Assert.Equal("WAFER-001", index.BatchAt(0)!.WaferId);
        Assert.Equal("WAFER-001", index.BatchAt(3)!.WaferId);
        Assert.Equal("WAFER-002", index.BatchAt(4)!.WaferId);
        Assert.Equal("WAFER-003", index.BatchAt(11)!.WaferId);
        Assert.Equal("WAFER-002", index.BatchAtElapsed(TimeSpan.FromMilliseconds(1500))!.WaferId);
    }

    [Fact]
    public void Empty_index_when_no_wafer_results()
    {
        using var writer = new RecordingWriter(new RecordingOptions
        {
            Directory = _dir,
            DeviceId = "AOI-01",
            ShiftId = "S1",
            DiskUsedRatioProvider = () => 0.1,
        });
        writer.Write(TwinTopic.Motion, new AxisSnapshot("ROBOT", "R", 0, 0, Base));

        var index = WaferBatchIndex.Build(Assert.Single(Directory.GetFiles(_dir, "*.aotrec")));

        Assert.Equal(0, index.Count);
        Assert.Null(index.BatchAt(0));
        Assert.Null(index.ByOrdinal(1));
    }

    /// <summary>计划 §4 T3.2 验收：随机 10 个批次跳转，首条消息必为该批次首帧。</summary>
    [Fact]
    public void Seek_random_ten_batches_first_message_is_batch_first_frame()
    {
        var file = BuildRecording(12);
        var hub = new RealtimeHub();
        using var session = new ReplaySession(file, hub) { Speed = 8 };

        var received = new List<WaferResult>();
        var firstArrived = new ManualResetEventSlim(false);
        using var sub = hub.Subscribe<WaferResult>(TwinTopic.Result, m =>
        {
            lock (received)
            {
                received.Add(m);
            }

            firstArrived.Set();
        });

        var picks = new[] { 0, 11, 5, 3, 9, 1, 7, 2, 10, 6 }; // 覆盖首/尾/中间，避免依赖随机源
        foreach (var pick in picks)
        {
            var batch = session.Batches[pick];
            received.Clear();
            firstArrived.Reset();

            session.SeekToBatch(batch);
            Assert.Equal(batch.FirstFrameIndex, session.CurrentFrameIndex);
            Assert.Equal(batch.WaferId, session.CurrentBatch!.WaferId);

            session.Play();
            Assert.True(firstArrived.Wait(TimeSpan.FromSeconds(15)), $"批次 {batch.WaferId} 未在超时内回放出消息");
            session.Pause();

            Assert.Equal(batch.WaferId, received[0].WaferId);
        }
    }

    [Fact]
    public void SeekRelativeBatch_moves_one_batch_and_clamps_at_edges()
    {
        var file = BuildRecording(4);
        using var session = new ReplaySession(file, new RealtimeHub());

        // 第 0 帧即属于第 1 批（批次区间从上一片结果之后开始）
        Assert.Equal("WAFER-001", session.CurrentBatch!.WaferId);

        Assert.Equal("WAFER-002", session.SeekRelativeBatch(1)!.WaferId);
        Assert.Equal("WAFER-003", session.SeekRelativeBatch(1)!.WaferId);
        Assert.Equal("WAFER-004", session.SeekRelativeBatch(1)!.WaferId);
        Assert.Null(session.SeekRelativeBatch(1)); // 尾批次之后再无批次
        Assert.Equal("WAFER-004", session.CurrentBatch!.WaferId);

        Assert.Equal("WAFER-003", session.SeekRelativeBatch(-1)!.WaferId);
        Assert.Equal("WAFER-002", session.SeekRelativeBatch(-1)!.WaferId);
    }

    [Fact]
    public void Session_speed_and_progress_report_position()
    {
        var file = BuildRecording(3);
        using var session = new ReplaySession(file, new RealtimeHub());

        session.Speed = 4.0;
        Assert.Equal(4.0, session.Speed);
        Assert.Throws<ArgumentOutOfRangeException>(() => session.Speed = 20);

        ReplayProgress? last = null;
        session.Progress += p => last = p;

        session.Seek(TimeSpan.FromSeconds(1));
        Assert.NotNull(last);
        Assert.Equal(4, last!.FrameIndex); // 第 2 批首帧是文件第 4 帧
        Assert.Equal(TimeSpan.FromSeconds(1), last.Position);
        Assert.Equal(TimeSpan.FromSeconds(2.5), last.Total);
    }
}
