using System.Collections.Concurrent;
using AoiTwin.Adapters;
using AoiTwin.Core.Bus;
using Xunit;

namespace AoiTwin.Adapters.Tests;

/// <summary>T2.3 适配器基类：健康状态机全迁移、指数退避 1s→30s、异常隔离。</summary>
public class AdapterBaseTests
{
    /// <summary>可控 Fake：连接失败 N 次后成功；记录每次重连等待秒数。ReceiveCore 自节流（与真实适配器一致，避免忙自旋）。</summary>
    private sealed class FakeAdapter(int failConnectTimes = 0, bool failReceive = false) : AdapterBase("FAKE")
    {
        public List<int> BackoffSeconds { get; } = [];
        public int ConnectCount;
        public int ReceiveCount;
        private int _failsLeft = failConnectTimes;

        protected override void ConnectCore(CancellationToken cancellationToken)
        {
            Interlocked.Increment(ref ConnectCount);
            if (Interlocked.Decrement(ref _failsLeft) >= 0)
            {
                throw new IOException("连接失败（模拟）");
            }
        }

        protected override void ReceiveCore(CancellationToken cancellationToken)
        {
            Interlocked.Increment(ref ReceiveCount);
            if (failReceive && ReceiveCount == 2)
            {
                throw new IOException("接收中断（模拟）");
            }

            Emit(TwinTopic.Motion, new AxisSnapshot("ROBOT", "R", ReceiveCount, 0, DateTimeOffset.UtcNow));
            cancellationToken.WaitHandle.WaitOne(1);
        }

        protected override Task DelayAsync(int seconds, CancellationToken cancellationToken)
        {
            BackoffSeconds.Add(seconds);
            return Task.CompletedTask;
        }
    }

    private static void WaitFor(Func<bool> condition, int timeoutMs = 15000)
    {
        var sw = System.Diagnostics.Stopwatch.StartNew();
        while (!condition() && sw.ElapsedMilliseconds < timeoutMs)
        {
            Thread.Sleep(10);
        }

        Assert.True(condition(), $"等待条件超时（{timeoutMs}ms）");
    }

    [Fact]
    public void Health_state_machine_full_transitions()
    {
        using var adapter = new FakeAdapter(failConnectTimes: 1);
        var states = new List<AdapterHealth>();
        adapter.HealthChanged += (_, h) => states.Add(h);

        adapter.Start();
        WaitFor(() => adapter.Health == AdapterHealth.Connected);
        adapter.Stop();

        Assert.Equal(
            [AdapterHealth.Connecting, AdapterHealth.Reconnecting, AdapterHealth.Connecting, AdapterHealth.Connected, AdapterHealth.Stopped],
            states);
        Assert.Equal([1], adapter.BackoffSeconds); // 第一次重连等 1s（模拟中不真实等待）
    }

    [Fact]
    public void Backoff_grows_to_30s_cap()
    {
        using var adapter = new FakeAdapter(failConnectTimes: 8);
        adapter.Start();
        WaitFor(() => adapter.ConnectCount >= 9);
        adapter.Stop();

        Assert.Equal([1, 2, 4, 8, 16, 30, 30, 30], adapter.BackoffSeconds);
    }

    [Fact]
    public void Receive_exception_triggers_reconnect_not_exit()
    {
        using var adapter = new FakeAdapter(failReceive: true);
        var received = 0;
        adapter.MessageEmitted += (_, _) => Interlocked.Increment(ref received);

        adapter.Start();
        WaitFor(() => adapter.ReceiveCount >= 3);
        adapter.Stop();

        Assert.True(adapter.Health is AdapterHealth.Connected or AdapterHealth.Stopped);
        Assert.True(adapter.ReceiveCount >= 3, "接收异常后必须继续循环而不是退出");
    }

    [Fact]
    public void Subscriber_exception_isolated()
    {
        using var adapter = new FakeAdapter();
        var good = 0;
        adapter.MessageEmitted += (_, _) => throw new InvalidOperationException("坏订阅方");
        adapter.MessageEmitted += (_, _) => Interlocked.Increment(ref good);

        adapter.Start();
        WaitFor(() => Volatile.Read(ref good) >= 3);
        adapter.Stop();

        Assert.True(good >= 3, "订阅方异常不得影响后续分发");
    }

    [Fact]
    public void Start_is_idempotent_and_stop_before_start_is_safe()
    {
        var adapter = new FakeAdapter();
        adapter.Stop(); // 未启动时 Stop 安全
        adapter.Start();
        adapter.Start();
        WaitFor(() => adapter.ReceiveCount >= 1);
        adapter.Stop();
        adapter.Stop();

        Assert.Equal(AdapterHealth.Stopped, adapter.Health);
    }
}

/// <summary>T2.3 四路适配器：报文 → 统一数据模型的单位换算用例。</summary>
public class AdapterParseTests
{
    [Fact]
    public void Plc_parse_axis_um_to_mm_signed()
    {
        // 12,500 µm = 0x30D4 = 12.5 mm；负值 -7500 µm = 0xFFFFE2B4 = -7.5 mm
        var mapping = new PlcAxisMapping("ROBOT", "Z", 100);
        var positive = PlcAdapter.ParseAxis(mapping, [0, (short)0x30D4, 0, 100], Base());
        Assert.Equal((12.5, 0.1), (positive.Position, positive.Velocity));

        var block = new short[] { unchecked((short)0xFFFF), unchecked((short)0xE2B4), 0, 0 };
        var negative = PlcAdapter.ParseAxis(mapping, block, Base());
        Assert.Equal(-7.5, negative.Position, precision: 3);
    }

    [Fact]
    public void Camera_sdk_event_maps_to_optics()
    {
        var evt = CameraSdkAdapter.Parse(
            new RawCameraEvent(RawCameraEventKind.CameraParam, 2, 12.5, Base()));
        Assert.Equal((TwinTopic.Optics, OpticsKind.CameraParam, 2, 12.5), (TwinTopic.Optics, evt.Kind, evt.Channel, evt.Value));
    }

    [Fact]
    public void Result_queue_px_to_um_and_tact()
    {
        var envelope = new ResultEnvelope(
            "W-0001",
            SizeX: 64,
            SizeY: 64,
            DieMap: [1, 2, 3, 4],
            Defects:
            [
                new RawDefect(1, Px: 100, Py: 200, SizePx: 3.5, DefectClass: "particle", GrayLevel: 42),
            ],
            TactMs: 31_500,
            CapturedAt: Base());

        var result = ResultQueueAdapter.Parse(envelope, pixelSizeUm: 0.5);
        Assert.Equal("W-0001", result.WaferId);
        var defect = Assert.Single(result.Defects);
        Assert.Equal((50.0, 100.0, 1.75), (defect.X, defect.Y, defect.SizeUm));
        Assert.Equal(TimeSpan.FromSeconds(31.5), result.Tact);
        Assert.Equal([1, 2, 3, 4], result.Map.DieMap);
    }

    [Fact]
    public void Result_queue_rejects_invalid_pixel_size()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            new ResultQueueAdapter(new StubResultQueue(), pixelSizeUm: 0));
    }

    private static DateTimeOffset Base() => new(2026, 11, 3, 8, 0, 0, TimeSpan.Zero);

    private sealed class StubResultQueue : IResultQueueTransport
    {
        public void Connect(CancellationToken cancellationToken) { }
        public bool TryTake(out ResultEnvelope envelope)
        {
            envelope = null!;
            return false;
        }
        public void Dispose() { }
    }
}

/// <summary>四路适配器端到端：Fake 传输 → 适配器 → 消息出口（含 Topic 1:1 映射校验）。</summary>
public class AdapterEndToEndTests
{
    [Fact]
    public void Plc_adapter_pumps_axis_snapshots()
    {
        var transport = new FakePlc();
        using var adapter = new PlcAdapter(transport, [new PlcAxisMapping("ROBOT", "R", 0), new PlcAxisMapping("ROBOT", "Theta", 4)], pollInterval: TimeSpan.FromMilliseconds(1));
        var messages = new ConcurrentQueue<(TwinTopic Topic, ITwinMessage Msg)>();
        adapter.MessageEmitted += (t, m) => messages.Enqueue((t, m));

        adapter.Start();
        WaitFor(() => messages.Count >= 4);
        adapter.Stop();

        Assert.All(messages.Take(4), m => Assert.Equal(TwinTopic.Motion, m.Topic));
        Assert.Contains(messages, m => Assert.IsType<AxisSnapshot>(m.Msg) is { Position: 12.5 });
    }

    [Fact]
    public void Light_adapter_pumps_all_channels()
    {
        var transport = new FakeLight(channelCount: 8);
        using var adapter = new LightSourceAdapter(transport, pollInterval: TimeSpan.FromMilliseconds(1));
        var messages = new ConcurrentQueue<(TwinTopic Topic, ITwinMessage Msg)>();
        adapter.MessageEmitted += (t, m) => messages.Enqueue((t, m));

        adapter.Start();
        WaitFor(() => messages.Count >= 8);
        adapter.Stop();

        var channels = messages.Take(8).Select(m => Assert.IsType<OpticsEvent>(m.Msg)).ToList();
        Assert.Equal(Enumerable.Range(1, 8), channels.Select(c => c.Channel));
        Assert.All(channels, c => Assert.Equal(OpticsKind.LightBrightness, c.Kind));
    }

    [Fact]
    public void Result_queue_adapter_drains_envelopes()
    {
        var transport = new FakeResultQueue();
        using var adapter = new ResultQueueAdapter(transport, pixelSizeUm: 1.0, pollInterval: TimeSpan.FromMilliseconds(1));
        var messages = new ConcurrentQueue<(TwinTopic Topic, ITwinMessage Msg)>();
        adapter.MessageEmitted += (t, m) => messages.Enqueue((t, m));

        adapter.Start();
        WaitFor(() => messages.Count >= 2);
        adapter.Stop();

        Assert.All(messages, m => Assert.Equal(TwinTopic.Result, m.Topic));
        Assert.Equal(2, transport.DrainedCount);
    }

    private static void WaitFor(Func<bool> condition, int timeoutMs = 15000)
    {
        var sw = System.Diagnostics.Stopwatch.StartNew();
        while (!condition() && sw.ElapsedMilliseconds < timeoutMs)
        {
            Thread.Sleep(10);
        }

        Assert.True(condition(), $"等待条件超时（{timeoutMs}ms）");
    }

    private sealed class FakePlc : IPlcTransport
    {
        public void Connect(CancellationToken cancellationToken) { }

        public short[] ReadBlock(int startAddress, int length, CancellationToken cancellationToken)
            => startAddress == 0 ? [0, (short)0x30D4, 0, 100] : [0, 0, 0, 0];

        public void Dispose() { }
    }

    private sealed class FakeLight(int channelCount) : ILightTransport
    {
        public int ChannelCount { get; } = channelCount;
        public void Connect(CancellationToken cancellationToken) { }
        public int ReadChannelBrightness(int channel, CancellationToken cancellationToken) => channel * 10;
        public void Dispose() { }
    }

    private sealed class FakeResultQueue : IResultQueueTransport
    {
        private readonly ConcurrentQueue<ResultEnvelope> _queue = new(
        [
            new ResultEnvelope("W-0001", 2, 2, [1, 0, 0, 1], [], 30_000, DateTimeOffset.UtcNow),
            new ResultEnvelope("W-0002", 2, 2, [0, 1, 1, 0], [], 31_000, DateTimeOffset.UtcNow),
        ]);

        public int DrainedCount { get; private set; }
        public void Connect(CancellationToken cancellationToken) { }
        public bool TryTake(out ResultEnvelope envelope)
        {
            if (_queue.TryDequeue(out envelope!))
            {
                DrainedCount++;
                return true;
            }

            return false;
        }
        public void Dispose() { }
    }
}
