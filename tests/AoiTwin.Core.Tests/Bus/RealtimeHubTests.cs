using AoiTwin.Core.Bus;
using Xunit;

namespace AoiTwin.Core.Tests.Bus;

/// <summary>T2.1 RealtimeHub 测试：广播、类型/Topic 过滤、异常隔离、退订、快照语义、吞吐冒烟。</summary>
public class RealtimeHubTests
{
    private static AxisSnapshot Axis(double pos = 1.0) =>
        new("ROBOT", "R", pos, 0, DateTimeOffset.Now);

    [Fact]
    public void Publish_broadcasts_to_all_subscribers_of_same_topic_and_type()
    {
        var hub = new RealtimeHub();
        var received = new List<double>();
        using var s1 = hub.Subscribe<AxisSnapshot>(TwinTopic.Motion, m => received.Add(m.Position));
        using var s2 = hub.Subscribe<AxisSnapshot>(TwinTopic.Motion, m => received.Add(m.Position));

        hub.Publish(TwinTopic.Motion, Axis(3.5));

        Assert.Equal([3.5, 3.5], received);
    }

    [Fact]
    public void Publish_filters_by_topic()
    {
        var hub = new RealtimeHub();
        var motionCount = 0;
        var opticsCount = 0;
        using var sm = hub.Subscribe<AxisSnapshot>(TwinTopic.Motion, _ => motionCount++);
        using var so = hub.Subscribe<OpticsEvent>(TwinTopic.Optics, _ => opticsCount++);

        hub.Publish(TwinTopic.Motion, Axis());

        Assert.Equal(1, motionCount);
        Assert.Equal(0, opticsCount);
    }

    [Fact]
    public void Publish_filters_by_message_type_on_same_topic()
    {
        var hub = new RealtimeHub();
        var axisCount = 0;
        var statusCount = 0;
        using var sa = hub.Subscribe<AxisSnapshot>(TwinTopic.Status, _ => axisCount++);
        using var ss = hub.Subscribe<StatusEvent>(TwinTopic.Status, _ => statusCount++);

        hub.Publish(TwinTopic.Status, new StatusEvent("TRACK", "WAFER_PRESENT", Severity.Info, null, DateTimeOffset.Now));

        Assert.Equal(0, axisCount);
        Assert.Equal(1, statusCount);
    }

    [Fact]
    public void Handler_exception_is_isolated_and_does_not_stop_other_subscribers()
    {
        var hub = new RealtimeHub();
        var received = new List<double>();
        using var bad = hub.Subscribe<AxisSnapshot>(TwinTopic.Motion, _ => throw new InvalidOperationException("boom"));
        using var good = hub.Subscribe<AxisSnapshot>(TwinTopic.Motion, m => received.Add(m.Position));

        hub.Publish(TwinTopic.Motion, Axis(7));

        Assert.Equal([7], received); // good 订阅方仍收到，Publish 不抛
    }

    [Fact]
    public void Dispose_stops_delivery()
    {
        var hub = new RealtimeHub();
        var count = 0;
        var sub = hub.Subscribe<AxisSnapshot>(TwinTopic.Motion, _ => count++);
        sub.Dispose();
        sub.Dispose(); // 重复 Dispose 安全

        hub.Publish(TwinTopic.Motion, Axis());

        Assert.Equal(0, count);
    }

    [Fact]
    public void Subscribe_during_publish_sees_snapshot_semantics()
    {
        var hub = new RealtimeHub();
        var firstCount = 0;
        var secondCount = 0;
        using var s1 = hub.Subscribe<AxisSnapshot>(TwinTopic.Motion, _ => { firstCount++; hub.Subscribe<AxisSnapshot>(TwinTopic.Motion, _ => secondCount++); });

        hub.Publish(TwinTopic.Motion, Axis());
        hub.Publish(TwinTopic.Motion, Axis());

        // 首次发布时新增的订阅不参与本次分发（快照），第二次发布起生效
        Assert.Equal(2, firstCount);
        Assert.Equal(1, secondCount);
    }

    [Fact]
    public void Publish_null_message_throws()
    {
        var hub = new RealtimeHub();
        Assert.Throws<ArgumentNullException>(() =>
            hub.Publish(TwinTopic.Motion, (AxisSnapshot)null!));
    }

    [Fact]
    public void Subscribe_null_handler_throws()
    {
        var hub = new RealtimeHub();
        Assert.Throws<ArgumentNullException>(() =>
            hub.Subscribe<AxisSnapshot>(TwinTopic.Motion, null!));
    }

    [Fact]
    public void Throughput_smoke_100k_messages()
    {
        // 性能基线（NFR-03）：单条发布—订阅开销 ≤ 0.1ms。CI 放宽到 5s/10 万条（0.05ms/条）防抖动。
        var hub = new RealtimeHub();
        var count = 0;
        using var s = hub.Subscribe<AxisSnapshot>(TwinTopic.Motion, _ => count++);

        var sw = System.Diagnostics.Stopwatch.StartNew();
        for (var i = 0; i < 100_000; i++)
        {
            hub.Publish(TwinTopic.Motion, Axis(i));
        }

        sw.Stop();
        Assert.Equal(100_000, count);
        Assert.True(sw.ElapsedMilliseconds < 5_000, $"吞吐不达标: {sw.ElapsedMilliseconds}ms / 100k 条");
    }
}
