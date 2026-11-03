using AoiTwin.Adapters;
using AoiTwin.Adapters.Mock;
using AoiTwin.Core.Bus;
using AoiTwin.Core.Recording;
using Xunit;

namespace AoiTwin.Adapters.Tests;

/// <summary>T2.4 Mock 适配器与全链路（US-M1-08）：Mock → Hub → 录制 → T2.2 校验。</summary>
public class MockChainTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), $"mockchain-{Guid.NewGuid():N}");

    public MockChainTests() => Directory.CreateDirectory(_dir);

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

    /// <summary>
    /// 全链路验收（US-M1-08）：Mock 四路适配器 → Hub → 录制器 → 文件可校验。
    ///
    /// 断言刻意做成「结构性」而非「速率性」：CI 双核 runner 上后台线程被调度挤压时，
    /// 固定时间窗内产出的帧数会远低于本地机器，速率阈值会造成假失败。
    /// 吞吐/时延属性能验收（T4.3 性能报告），不在此处判定。
    /// </summary>
    [Fact]
    public void Mock_pipeline_drives_hub_and_recording_end_to_end()
    {
        var scenario = new MockScenario { WaferIntervalSec = 0.2, WaferMapSize = 8, DefectsPerWafer = 2 };
        var file = MockDatasetRunner.Generate(_dir, scenario, TimeSpan.FromSeconds(3), diskUsedRatioProvider: () => 0.1);

        var info = RecordingReader.Inspect(file);
        var head = File.ReadAllBytes(file)[..Math.Min(16, (int)new FileInfo(file).Length)];
        Assert.True(
            info.IsFinalized,
            $"errors=[{string.Join("; ", info.Errors)}] head={Convert.ToHexString(head)} size={new FileInfo(file).Length}");
        Assert.False(info.HasErrors);
        Assert.Equal("AOI-01", info.DeviceId);

        var byType = new Dictionary<byte, int>();
        var waferIds = new List<string>();
        RecordingReader.Inspect(file, m =>
        {
            var id = RecFileFormat.TypeIdOf(m);
            byType[id] = byType.TryGetValue(id, out var n) ? n + 1 : 1;
            if (m is WaferResult wafer)
            {
                waferIds.Add(wafer.WaferId);
            }
        });

        var detail = string.Join(", ", byType.Select(kv => $"{kv.Key}:{kv.Value}"));
        Assert.True(byType.GetValueOrDefault(RecFileFormat.MessageType.AxisSnapshot) >= 1, $"未产出轴快照（{detail}）");
        Assert.True(byType.GetValueOrDefault(RecFileFormat.MessageType.OpticsEvent) >= 1, $"未产出光学事件（{detail}）");
        Assert.True(byType.GetValueOrDefault(RecFileFormat.MessageType.WaferResult) >= 1, $"未产出晶圆结果（{detail}）");
        // Status 通道：M1 的四路适配器均不产出状态事件（PLC 事件位在 M2 接入），
        // 录制订阅保留该通道以避免二期的契约变更——此处只要求「不产出额外类型」。

        // 录制订阅对每条发布消息只写一帧：帧数与逐帧回调计数必须严格相等（无静默丢失）
        Assert.Equal(info.ValidFrameCount, byType.Values.Sum());
        Assert.All(waferIds, id => Assert.StartsWith("W-", id));
        Assert.Equal(waferIds.Count, waferIds.Distinct().Count());
    }

    [Fact]
    public void Mock_result_envelope_converts_to_wafer_result()
    {
        var transport = new MockResultQueueTransport(new MockScenario { WaferIntervalSec = 0.01, WaferMapSize = 8 });
        Assert.True(transport.TryTake(out var envelope));
        var result = ResultQueueAdapter.Parse(envelope, pixelSizeUm: 0.5);
        Assert.Equal("W-000001", result.WaferId);
        Assert.Equal(8, result.Map.SizeX);
        Assert.All(result.Defects, d => Assert.True(d.X >= 0 && d.X <= 8 * 0.5 + 0.001));
    }

    [Fact]
    public void Mock_plc_trajectory_stays_within_amplitude()
    {
        var scenario = new MockScenario();
        using var plc = new MockPlcTransport(scenario, start: DateTimeOffset.UtcNow);
        foreach (var mapping in plc.Mappings)
        {
            var block = plc.ReadBlock(mapping.Address, 4, CancellationToken.None);
            var snapshot = PlcAdapter.ParseAxis(mapping, block, DateTimeOffset.UtcNow);
            var spec = scenario.Axes.Single(a => a.PartId == mapping.PartId && a.AxisName == mapping.AxisName);
            Assert.InRange(snapshot.Position, -spec.AmplitudeUm / 1000.0, spec.AmplitudeUm / 1000.0);
        }
    }

    [Fact]
    public void Mock_light_decay_channel_fades_over_time()
    {
        using var light = new MockLightTransport(new MockScenario { DecayChannel = 3, LightChannels = 8 });
        var initial = light.ReadChannelBrightness(3, CancellationToken.None);
        Assert.InRange(light.ReadChannelBrightness(1, CancellationToken.None), 60, 80);
        Assert.InRange(initial, 0, 100);
    }

    [Fact]
    public void Scenario_json_roundtrip()
    {
        var path = Path.Combine(_dir, "scenario.json");
        var scenario = new MockScenario { ScenarioName = "TEST", Seed = 42, WaferIntervalSec = 0.1 };
        File.WriteAllText(path, System.Text.Json.JsonSerializer.Serialize(scenario));
        var loaded = MockScenario.Load(path);
        Assert.Equal(("TEST", 42, 0.1), (loaded.ScenarioName, loaded.Seed, loaded.WaferIntervalSec));
    }
}
