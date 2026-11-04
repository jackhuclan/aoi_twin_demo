using System.Text;
using AoiTwin.Core.Bus;
using Xunit;

namespace AoiTwin.Core.Tests.Bus;

/// <summary>
/// TA.1 节流分发（PRD-AOI-TWIN-M2 FR-M2-01 / §5.1）：
/// 轴数据合并窗口保留最新值；TimeSpan.Zero/null 直推；KeepLatest=false 逐条排队；
/// 策略配置文件热加载，坏配置保留旧值。
/// </summary>
public class ThrottlePolicyTests : IDisposable
{
    private readonly RealtimeHub _hub = new();
    private readonly string _dir;
    private readonly string _configPath;

    public ThrottlePolicyTests()
    {
        _dir = Path.Combine(Path.GetTempPath(), "aoitwin-throttle-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_dir);
        _configPath = Path.Combine(_dir, "throttle-policy.json");
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

    [Fact]
    public void Null_policy_and_zero_window_deliver_synchronously_on_publisher_thread()
    {
        var delivered = new List<(int ThreadId, double Position)>();
        using var s1 = _hub.Subscribe<AxisSnapshot>(TwinTopic.Motion, m => delivered.Add((Environment.CurrentManagedThreadId, m.Position)));

        _hub.Publish(TwinTopic.Motion, Snap(1));
        _hub.Publish(TwinTopic.Motion, Snap(2));

        // 直推：逐条同步送达，且回调在发布方线程执行（M1 线程模型不变）
        Assert.Equal(2, delivered.Count);
        Assert.All(delivered, d => Assert.Equal(Environment.CurrentManagedThreadId, d.ThreadId));
        Assert.Equal(2, delivered[1].Position);
    }

    [Fact]
    public async Task Merge_window_keeps_only_latest_value()
    {
        var delivered = new List<double>();
        using var gate = new ManualResetEventSlim(false);
        using var s = _hub.Subscribe<AxisSnapshot>(
            TwinTopic.Motion,
            m =>
            {
                lock (delivered)
                {
                    delivered.Add(m.Position);
                    gate.Set();
                }
            },
            new ThrottlePolicy(TimeSpan.FromMilliseconds(120)));

        // 窗口内连发 5 条：全部被合并，只送出最新一条
        for (var i = 1; i <= 5; i++)
        {
            _hub.Publish(TwinTopic.Motion, Snap(i));
        }

        Assert.Empty(delivered); // 窗口未到，不应有任何送达

        Assert.True(gate.Wait(TimeSpan.FromSeconds(3)), "窗口结束后应送达一次");
        await Task.Delay(200); // 再等一段时间确认不会重复送达
        Assert.Single(delivered);
        Assert.Equal(5, delivered[0]); // 保留的是最新值
    }

    [Fact]
    public async Task KeepLatest_false_drains_queue_in_order()
    {
        var delivered = new List<double>();
        using var all = new ManualResetEventSlim(false);
        using var s = _hub.Subscribe<AxisSnapshot>(
            TwinTopic.Motion,
            m =>
            {
                lock (delivered)
                {
                    delivered.Add(m.Position);
                    if (delivered.Count >= 5)
                    {
                        all.Set();
                    }
                }
            },
            new ThrottlePolicy(TimeSpan.FromMilliseconds(40), KeepLatest: false));

        for (var i = 1; i <= 5; i++)
        {
            _hub.Publish(TwinTopic.Motion, Snap(i));
        }

        Assert.True(all.Wait(TimeSpan.FromSeconds(3)), "队列应按间隔逐条排空");
        Assert.Equal(new double[] { 1, 2, 3, 4, 5 }, delivered); // 逐条排队：顺序完整
        await Task.Delay(150);
        Assert.Equal(5, delivered.Count); // 排空后不再多送
    }

    [Fact]
    public async Task Unsubscribe_stops_delivery()
    {
        var count = 0;
        var sub = _hub.Subscribe<AxisSnapshot>(
            TwinTopic.Motion,
            _ => Interlocked.Increment(ref count),
            new ThrottlePolicy(TimeSpan.FromMilliseconds(60)));
        sub.Dispose();

        _hub.Publish(TwinTopic.Motion, Snap(1));
        await Task.Delay(250);

        Assert.Equal(0, count);
    }

    [Fact]
    public void File_source_loads_policies_and_defaults_missing_keys_to_null()
    {
        WriteConfig(/* lang=json */ """{ "Motion": { "MinIntervalMs": 200 }, "Status": null }""");
        using var source = new ThrottlePolicyFileSource(_configPath);

        Assert.Equal(TimeSpan.FromMilliseconds(200), source.Get("Motion")!.MinInterval);
        Assert.True(source.Get("Motion")!.KeepLatest);
        Assert.Null(source.Get("Status")); // 显式 null = 直推
        Assert.Null(source.Get("Optics")); // 缺失键 = 直推
    }

    [Fact]
    public async Task Hot_reload_picks_up_new_values()
    {
        WriteConfig("""{ "Motion": { "MinIntervalMs": 200 } }""");
        using var source = new ThrottlePolicyFileSource(_configPath);
        Assert.Equal(200, source.Get("Motion")!.MinInterval.TotalMilliseconds);

        WriteConfig("""{ "Motion": { "MinIntervalMs": 80 } }""");
        var deadline = DateTime.UtcNow.AddSeconds(5);
        while (source.Get("Motion")!.MinInterval != TimeSpan.FromMilliseconds(80) && DateTime.UtcNow < deadline)
        {
            await Task.Delay(50);
        }

        Assert.Equal(80, source.Get("Motion")!.MinInterval.TotalMilliseconds);
    }

    [Fact]
    public async Task Corrupt_config_keeps_previous_policy()
    {
        WriteConfig("""{ "Motion": { "MinIntervalMs": 200 } }""");
        using var source = new ThrottlePolicyFileSource(_configPath);
        Assert.Equal(200, source.Get("Motion")!.MinInterval.TotalMilliseconds);

        File.WriteAllText(_configPath, "{ 这不是合法 JSON", Encoding.UTF8);
        var deadline = DateTime.UtcNow.AddSeconds(3);
        while (source.LastError is null && DateTime.UtcNow < deadline)
        {
            await Task.Delay(50);
        }

        Assert.NotNull(source.LastError); // 坏配置被记录
        Assert.Equal(200, source.Get("Motion")!.MinInterval.TotalMilliseconds); // 旧策略继续生效
    }

    private static AxisSnapshot Snap(double position) =>
        new("ROBOT", "R", position, 0, DateTimeOffset.UtcNow);

    private void WriteConfig(string json) =>
        File.WriteAllText(_configPath, json, Encoding.UTF8);
}
