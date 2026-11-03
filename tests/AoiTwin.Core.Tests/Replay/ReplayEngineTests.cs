using System.Text;
using AoiTwin.Core.Bus;
using AoiTwin.Core.Replay;
using AoiTwin.Core.Recording;
using Xunit;

namespace AoiTwin.Core.Tests.Replay;

/// <summary>T3.1 回放引擎测试：逐帧一致性（核心验收）、变速顺序不变、Seek 二分与回放时钟、暂停续播。</summary>
public class ReplayEngineTests : IDisposable
{
    private static readonly DateTimeOffset Base = new(2026, 11, 3, 8, 0, 0, TimeSpan.Zero);

    private readonly string _dir;

    public ReplayEngineTests()
    {
        _dir = Path.Combine(Path.GetTempPath(), $"replay-{Guid.NewGuid():N}");
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

    private sealed record Captured(TwinTopic Topic, byte TypeId, ITwinMessage Message);

    /// <summary>订阅 Hub 全部 Topic × 全部四类消息。</summary>
    private List<Captured> CaptureAll(RealtimeHub hub)
    {
        var list = new List<Captured>();
        var topics = new[] { TwinTopic.Motion, TwinTopic.Status, TwinTopic.Optics, TwinTopic.Result };
        foreach (var topic in topics)
        {
            hub.Subscribe<AxisSnapshot>(topic, m => list.Add(new(topic, RecFileFormat.TypeIdOf(m), m)));
            hub.Subscribe<StatusEvent>(topic, m => list.Add(new(topic, RecFileFormat.TypeIdOf(m), m)));
            hub.Subscribe<OpticsEvent>(topic, m => list.Add(new(topic, RecFileFormat.TypeIdOf(m), m)));
            hub.Subscribe<WaferResult>(topic, m => list.Add(new(topic, RecFileFormat.TypeIdOf(m), m)));
        }

        return list;
    }

    /// <summary>构造录制：axisCount 帧轴快照（间隔 100ms）+ 1 条状态事件。</summary>
    private (string File, List<ITwinMessage> Messages) BuildRecording(int axisCount)
    {
        var messages = new List<ITwinMessage>();
        using var writer = new RecordingWriter(new RecordingOptions
        {
            Directory = _dir,
            DeviceId = "AOI-01",
            ShiftId = "S1",
            DiskUsedRatioProvider = () => 0.1,
        });
        for (var i = 0; i < axisCount; i++)
        {
            var axis = new AxisSnapshot("ROBOT", "R", i, 0.5, Base.AddMilliseconds(i * 100));
            writer.Write(TwinTopic.Motion, axis);
            messages.Add(axis);
        }

        var status = new StatusEvent("LOADPORT", "CASSETTE_LOADED", Severity.Info, null, Base.AddMilliseconds(axisCount * 100));
        writer.Write(TwinTopic.Status, status);
        messages.Add(status);

        var file = Assert.Single(Directory.GetFiles(_dir, "*.aotrec"));
        return (file, messages);
    }

    /// <summary>运行一次完整回放并返回捕获序列（独立 Hub）。</summary>
    private static (List<Captured> Captured, DateTimeOffset BeforePlay) RunReplay(string file, double speed)
    {
        var hub = new RealtimeHub();
        var captured = new List<Captured>();
        var topics = new[] { TwinTopic.Motion, TwinTopic.Status, TwinTopic.Optics, TwinTopic.Result };
        foreach (var topic in topics)
        {
            hub.Subscribe<AxisSnapshot>(topic, m => captured.Add(new(topic, RecFileFormat.TypeIdOf(m), m)));
            hub.Subscribe<StatusEvent>(topic, m => captured.Add(new(topic, RecFileFormat.TypeIdOf(m), m)));
            hub.Subscribe<OpticsEvent>(topic, m => captured.Add(new(topic, RecFileFormat.TypeIdOf(m), m)));
            hub.Subscribe<WaferResult>(topic, m => captured.Add(new(topic, RecFileFormat.TypeIdOf(m), m)));
        }

        using var engine = new ReplayEngine(file, hub);
        var done = new ManualResetEventSlim(false);
        engine.Completed += () => done.Set();
        engine.Speed = speed;
        var beforePlay = DateTimeOffset.UtcNow;
        engine.Play();
        Assert.True(done.Wait(TimeSpan.FromSeconds(15)), "回放未在超时内完成");
        return (captured, beforePlay);
    }

    /// <summary>逐类型比较非时间戳字段（回放时钟偏移使 Timestamp 必然不同）。</summary>
    private static void AssertSameContent(ITwinMessage expected, ITwinMessage actual)
    {
        switch (expected, actual)
        {
            case (AxisSnapshot e, AxisSnapshot a):
                Assert.Equal((e.PartId, e.AxisName, e.Position, e.Velocity), (a.PartId, a.AxisName, a.Position, a.Velocity));
                break;
            case (StatusEvent e, StatusEvent a):
                Assert.Equal((e.PartId, e.EventCode, e.Severity), (a.PartId, a.EventCode, a.Severity));
                Assert.Equal(e.Payload.Count, a.Payload.Count);
                foreach (var kv in e.Payload)
                {
                    Assert.True(a.Payload.TryGetValue(kv.Key, out var v) && v == kv.Value, $"Payload[{kv.Key}] 不符");
                }

                break;
            case (OpticsEvent e, OpticsEvent a):
                Assert.Equal((e.PartId, e.Kind, e.Channel, e.Value), (a.PartId, a.Kind, a.Channel, a.Value));
                break;
            case (WaferResult e, WaferResult a):
                Assert.Equal((e.WaferId, e.Tact), (a.WaferId, a.Tact));
                Assert.Equal(e.Map.SizeX, a.Map.SizeX);
                Assert.Equal(e.Map.SizeY, a.Map.SizeY);
                Assert.Equal(e.Defects.Count, a.Defects.Count);
                break;
            default:
                Assert.Fail($"消息类型不匹配: 期望 {expected.GetType().Name} 实际 {actual.GetType().Name}");
                break;
        }
    }

    [Fact]
    public void Replay_sequence_matches_recording_frame_by_frame()
    {
        var (file, messages) = BuildRecording(axisCount: 20);
        var index = RecordingReader.BuildIndex(file);
        Assert.Equal(messages.Count, index.Count);

        var (captured, beforePlay) = RunReplay(file, ReplayEngine.MaxSpeed);

        // 核心验收（PRD §7-3）：回放消息序列与录制文件逐条一致
        Assert.Equal(messages.Count, captured.Count);
        for (var i = 0; i < messages.Count; i++)
        {
            Assert.Equal(index[i].Topic, captured[i].Topic);
            Assert.Equal(RecFileFormat.TypeIdOf(messages[i]), captured[i].TypeId);
            AssertSameContent(messages[i], captured[i].Message);

            // 回放时钟：时间戳统一偏移为回放墙钟（不早于 Play 时刻）
            Assert.True(captured[i].Message.Timestamp >= beforePlay.AddMilliseconds(-100),
                $"帧#{i} 回放时间戳 {captured[i].Message.Timestamp:O} 早于播放起点");
        }
    }

    [Fact]
    public void Order_invariant_across_speeds()
    {
        var (file, messages) = BuildRecording(axisCount: 10);

        var (fast, _) = RunReplay(file, ReplayEngine.MaxSpeed);
        var (slow, _) = RunReplay(file, 1.0);

        Assert.Equal(messages.Count, fast.Count);
        Assert.Equal(fast.Count, slow.Count);
        for (var i = 0; i < fast.Count; i++)
        {
            Assert.Equal((fast[i].Topic, fast[i].TypeId), (slow[i].Topic, slow[i].TypeId));
            AssertSameContent(fast[i].Message, slow[i].Message);
        }
    }

    [Fact]
    public void Seek_positions_to_binary_search_target_and_offsets_to_replay_clock()
    {
        var (file, messages) = BuildRecording(axisCount: 20); // 总时长 2s，间隔 100ms
        var index = RecordingReader.BuildIndex(file);

        var hub = new RealtimeHub();
        var captured = new List<Captured>();
        hub.Subscribe<AxisSnapshot>(TwinTopic.Motion, m => captured.Add(new(TwinTopic.Motion, RecFileFormat.TypeIdOf(m), m)));
        hub.Subscribe<StatusEvent>(TwinTopic.Status, m => captured.Add(new(TwinTopic.Status, RecFileFormat.TypeIdOf(m), m)));

        using var engine = new ReplayEngine(file, hub);
        var done = new ManualResetEventSlim(false);
        engine.Completed += () => done.Set();
        engine.Speed = ReplayEngine.MaxSpeed;

        // 目标 950ms → 二分定位到首个 ≥ 950ms 的帧（i=10，elapsed=1000ms）
        engine.Seek(TimeSpan.FromMilliseconds(950));
        Assert.Equal(10, engine.CurrentFrameIndex);
        Assert.Equal(ReplayState.Paused, engine.State);

        var beforePlay = DateTimeOffset.UtcNow;
        engine.Play();
        Assert.True(done.Wait(TimeSpan.FromSeconds(15)), "Seek 后回放未在超时内完成");

        Assert.Equal(messages.Count - 10, captured.Count);
        Assert.Equal(index[10].Topic, captured[0].Topic);
        AssertSameContent(messages[10], captured[0].Message);

        // Seek 后时间戳统一偏移为回放时钟
        Assert.True(captured[0].Message.Timestamp >= beforePlay.AddMilliseconds(-100),
            $"Seek 后首帧回放时间戳 {captured[0].Message.Timestamp:O} 未偏移到回放时钟");
    }

    [Fact]
    public void Pause_stops_replay_and_resume_continues_from_same_frame()
    {
        var (file, messages) = BuildRecording(axisCount: 20);

        var hub = new RealtimeHub();
        var captured = new List<Captured>();
        hub.Subscribe<AxisSnapshot>(TwinTopic.Motion, m => captured.Add(new(TwinTopic.Motion, RecFileFormat.TypeIdOf(m), m)));

        using var engine = new ReplayEngine(file, hub);
        engine.Play(); // 1×：首帧立即出，第二帧 100ms 后

        Thread.Sleep(50);
        engine.Pause();
        var countAfterPause = captured.Count;
        Assert.True(countAfterPause <= 1, $"1× 下 50ms 内不应超过 1 帧，实际 {countAfterPause}");

        Thread.Sleep(150); // 暂停期间不应有新帧
        Assert.Equal(countAfterPause, captured.Count);
        Assert.Equal(ReplayState.Paused, engine.State);

        var done = new ManualResetEventSlim(false);
        engine.Completed += () => done.Set();
        engine.Play();
        Assert.True(done.Wait(TimeSpan.FromSeconds(15)), "续播未在超时内完成");

        Assert.Equal(messages.Count - 1, captured.Count); // 本测试只订阅了 Axis 消息
    }

    [Fact]
    public void Speed_out_of_range_rejected()
    {
        var (file, _) = BuildRecording(axisCount: 1);
        using var engine = new ReplayEngine(file, new RealtimeHub());
        Assert.Throws<ArgumentOutOfRangeException>(() => engine.Speed = 0.4);
        Assert.Throws<ArgumentOutOfRangeException>(() => engine.Speed = 8.1);
        engine.Speed = 0.5;
        engine.Speed = 8.0;
    }

    [Fact]
    public void Seek_backward_after_completed_allows_replay()
    {
        var (file, messages) = BuildRecording(axisCount: 5);

        var hub = new RealtimeHub();
        var captured = new List<Captured>();
        hub.Subscribe<AxisSnapshot>(TwinTopic.Motion, m => captured.Add(new(TwinTopic.Motion, RecFileFormat.TypeIdOf(m), m)));

        using var engine = new ReplayEngine(file, hub);
        var done = new ManualResetEventSlim(false);
        engine.Completed += () => done.Set();
        engine.Speed = ReplayEngine.MaxSpeed;
        engine.Play();
        Assert.True(done.Wait(TimeSpan.FromSeconds(15)));
        Assert.Equal(ReplayState.Completed, engine.State);
        var firstRunCount = captured.Count;

        engine.Seek(TimeSpan.Zero);
        Assert.Equal(ReplayState.Paused, engine.State);
        done.Reset();
        engine.Play();
        Assert.True(done.Wait(TimeSpan.FromSeconds(15)));
        Assert.Equal(firstRunCount * 2, captured.Count);
    }
}
