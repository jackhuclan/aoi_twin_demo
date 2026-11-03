using AoiTwin.Adapters;
using AoiTwin.Adapters.Mock;
using AoiTwin.Core.Bus;
using AoiTwin.Core.Recording;
using Xunit;

namespace AoiTwin.Adapters.Tests;

/// <summary>
/// T2.5 采集会话：四路状态/吞吐统计、录制开关、磁盘水位、单源故障隔离（NFR-05）。
/// 断言遵循与 T2.4 相同的原则：只判「结构性事实」，不判固定时间窗内的帧数（CI 慢速 runner 会假失败）。
/// </summary>
public class CollectionSessionTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), $"collect-{Guid.NewGuid():N}");

    public CollectionSessionTests() => Directory.CreateDirectory(_dir);

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

    /// <summary>可注入故障的单源：前两次正常发布状态事件，之后抛错（模拟拔线/设备进程被杀）。</summary>
    private sealed class FaultySource : AdapterBase
    {
        private int _receiveCount;

        public FaultySource()
            : base("FAULT")
        {
        }

        public int ReceiveCount => Volatile.Read(ref _receiveCount);

        protected override void ConnectCore(CancellationToken cancellationToken) { }

        protected override void ReceiveCore(CancellationToken cancellationToken)
        {
            var n = Interlocked.Increment(ref _receiveCount);
            if (n > 2)
            {
                throw new IOException("模拟数据源中断");
            }

            Emit(TwinTopic.Status, new StatusEvent(
                "STAGE", "LOADED", Severity.Info, null, DateTimeOffset.UtcNow));
            cancellationToken.WaitHandle.WaitOne(20);
        }

        // 缩短退避，避免测试等满 1s 才观察到 Reconnecting
        protected override Task DelayAsync(int seconds, CancellationToken cancellationToken)
            => Task.Delay(50, cancellationToken);
    }

    private static void WaitFor(Func<bool> condition, int timeoutMs = 15000)
    {
        var sw = System.Diagnostics.Stopwatch.StartNew();
        while (!condition() && sw.ElapsedMilliseconds < timeoutMs)
        {
            Thread.Sleep(20);
        }

        Assert.True(condition(), $"等待条件超时（{timeoutMs}ms）");
    }

    private static MockScenario SmallScenario() => new()
    {
        WaferIntervalSec = 0.2,
        WaferMapSize = 8,
        DefectsPerWafer = 2,
    };

    private RecordingOptions RecordingTo(string shiftId, double diskRatio = 0.1) => new()
    {
        Directory = _dir,
        DeviceId = "AOI-01",
        ShiftId = shiftId,
        DiskUsedRatioProvider = () => diskRatio,
    };

    [Fact]
    public void Session_reports_all_sources_and_topic_throughput()
    {
        var hub = new RealtimeHub();
        using var session = MockCollectionFactory.CreateSession(hub, SmallScenario());

        session.Start();
        // 等待条件必须是「每一路都产出」：CAMERA 有限频，Connected 先于首条消息，
        // 只等 Topic 有量就会让个别源 0 计数（CI 慢速调度下的假失败源）
        WaitFor(() => session.Snapshot().Sources.All(s => s.MessageCount > 0));

        var snapshot = session.Snapshot();

        Assert.True(snapshot.IsRunning);
        Assert.Equal(["PLC", "CAMERA", "LIGHT", "RESULT_QUEUE"], session.SourceNames);
        Assert.All(snapshot.Sources, s => Assert.Equal(AdapterHealth.Connected, s.Health));
        Assert.All(snapshot.Sources, s => Assert.True(s.MessageCount > 0, $"{s.Name} 未产出消息"));
        Assert.All(snapshot.Sources, s => Assert.NotNull(s.LastMessageAt));

        // Status 通道：M1 四路适配器均不产出状态事件（PLC 事件位在 M2 接入），其余三路必须有量
        Assert.Equal(0, snapshot.Topics.Single(t => t.Topic == TwinTopic.Status).Count);
        Assert.Equal(snapshot.Topics.Sum(t => t.Count), snapshot.TotalMessages);
        Assert.Equal(snapshot.Sources.Sum(s => s.MessageCount), snapshot.TotalMessages);
    }

    [Fact]
    public void Rate_is_positive_after_sample_window()
    {
        var hub = new RealtimeHub();
        using var session = MockCollectionFactory.CreateSession(hub, SmallScenario());

        session.Start();
        WaitFor(() => session.Snapshot().TotalMessages > 0);
        session.Snapshot(); // 建立窗口基线
        Thread.Sleep(400);
        var snapshot = session.Snapshot();

        var motion = snapshot.Topics.Single(t => t.Topic == TwinTopic.Motion);
        Assert.True(motion.RatePerSecond > 0, $"Motion 速率应为正，实际 {motion.RatePerSecond:F2}");
        Assert.True(snapshot.AggregateRatePerSecond >= motion.RatePerSecond);
    }

    [Fact]
    public void Single_source_fault_does_not_stop_other_channels()
    {
        var hub = new RealtimeHub();
        var good = MockCollectionFactory.BuildAdapters(SmallScenario())[0]; // PLC
        using var session = new CollectionSession(hub, [new FaultySource(), good]);

        session.Start();
        WaitFor(() => session.Snapshot().Sources[0].Health == AdapterHealth.Reconnecting);

        var afterFault = session.Snapshot();
        Thread.Sleep(400);
        var later = session.Snapshot();

        Assert.True(session.IsRunning, "单源故障不得终止采集会话");
        Assert.True(
            later.Sources[1].MessageCount > afterFault.Sources[1].MessageCount,
            "故障源以外的通道必须继续采集（NFR-05）");
        Assert.Equal(2, afterFault.Sources[0].MessageCount);
        Assert.Equal(2, later.Sources[0].MessageCount);
        Assert.True(later.Sources[0].IsFaulted);
        Assert.Equal("重连中", later.Sources[0].HealthText);
    }

    [Fact]
    public void Recording_writes_frames_and_finalizes_file()
    {
        var hub = new RealtimeHub();
        using var session = MockCollectionFactory.CreateSession(hub, SmallScenario(), RecordingTo("MOCK-REC"));

        session.Start();
        Assert.True(session.StartRecording());
        Assert.False(session.StartRecording()); // 幂等

        WaitFor(() => session.Snapshot().Recording.FrameCount >= 5);
        var whileRecording = session.Snapshot().Recording;
        Assert.True(whileRecording.IsRecording);
        Assert.NotNull(whileRecording.FilePath);
        Assert.True(whileRecording.BytesWritten > 0);
        Assert.Equal(_dir, whileRecording.Directory);

        session.Stop(); // 停采集：先停适配器再落 Footer

        var afterStop = session.Snapshot().Recording;
        Assert.False(afterStop.IsRecording);
        Assert.Null(session.StopRecording()); // 已停止时幂等返回 null

        var file = Assert.Single(Directory.GetFiles(_dir, "*.aotrec"));
        var info = RecordingReader.Inspect(file);
        Assert.True(info.IsFinalized, $"errors=[{string.Join("; ", info.Errors)}]");
        Assert.False(info.HasErrors);
        Assert.Equal("MOCK-REC", info.ShiftId);
        Assert.Equal(afterStop.FrameCount, info.ValidFrameCount);
        Assert.True(info.ValidFrameCount >= whileRecording.FrameCount);
    }

    [Fact]
    public void Disk_watermark_pauses_recording_and_counts_dropped_frames()
    {
        var hub = new RealtimeHub();
        using var session = MockCollectionFactory.CreateSession(
            hub, SmallScenario(), RecordingTo("MOCK-WATERMARK", diskRatio: 0.95));

        session.Start();
        session.StartRecording();

        WaitFor(() => session.Snapshot().Recording.DroppedFrameCount > 0);
        var snapshot = session.Snapshot();

        Assert.True(snapshot.DiskUsedRatio > 0.85);
        Assert.True(snapshot.Recording.PausedByDiskWatermark);
        Assert.Equal(0, snapshot.Recording.FrameCount);
        Assert.Null(snapshot.Recording.FilePath); // 从未写过盘，不应产生空文件
        Assert.Empty(Directory.GetFiles(_dir));

        session.Stop();
    }

    [Fact]
    public void Duplicate_source_names_are_rejected()
    {
        var hub = new RealtimeHub();
        var scenario = SmallScenario();
        var adapters = MockCollectionFactory.BuildAdapters(scenario);

        var ex = Assert.Throws<ArgumentException>(() => new CollectionSession(hub, [adapters[0], adapters[0]]));
        Assert.Contains("重复", ex.Message);
    }
}
