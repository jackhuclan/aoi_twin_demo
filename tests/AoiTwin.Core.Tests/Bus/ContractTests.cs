using System.Text.Json;
using AoiTwin.Core.Bus;
using AoiTwin.Core.Serialization;
using Xunit;

namespace AoiTwin.Core.Tests.Bus;

/// <summary>T1.0 契约冻结测试：消息结构与 JSON 往返是 .aotrec 文件格式的一部分，破坏即破坏录制格式。</summary>
public class ContractTests
{
    private const string TestPartId = "ROBOT";
    private static readonly DateTimeOffset Ts = new(2026, 11, 3, 9, 0, 0, TimeSpan.FromHours(8));

    private const int BaseTopicCount = 4;

    /// <summary>
    /// 契约重点不是「一共有几个 Topic」，而是**基值序号不变 + 新增只追加**：
    /// Topic 是 .aotrec 帧头里的一个字节，M1 录制的文件必须永远可读。
    /// M3 追加了 Alert（=4），此测试由「断言只有四个」改为「断言前四个值不变、后续只追加」。
    /// </summary>
    [Fact]
    public void TwinTopic_keeps_base_values_and_only_appends()
    {
        Assert.Equal(0, (int)TwinTopic.Motion);
        Assert.Equal(1, (int)TwinTopic.Status);
        Assert.Equal(2, (int)TwinTopic.Optics);
        Assert.Equal(3, (int)TwinTopic.Result);

        var values = Enum.GetValues<TwinTopic>();
        Assert.True(values.Length >= BaseTopicCount, "基值 Topic 不得被删除");

        // 追加项必须排在基值之后，且不得出现空洞（空洞意味着有人插入或删值）
        var expected = new TwinTopic[values.Length];
        for (var i = 0; i < values.Length; i++)
        {
            expected[i] = (TwinTopic)i;
        }

        Assert.Equal(expected, values);
        Assert.Contains(TwinTopic.Alert, values); // M3 增量
    }

    [Fact]
    public void AxisSnapshot_valid_fields_roundtrip_json()
    {
        var msg = new AxisSnapshot(TestPartId, "R", 12.5, 3.2, Ts);
        var json = JsonSerializer.Serialize(msg, TwinJson.Options);
        var back = JsonSerializer.Deserialize<AxisSnapshot>(json, TwinJson.Options);

        Assert.NotNull(back);
        Assert.Equal(msg, back); // record 值相等
        Assert.Equal(TestPartId, back!.PartId);
        Assert.Equal("R", back.AxisName);
        Assert.Equal(12.5, back.Position);
        Assert.Equal(Ts, back.Timestamp);
    }

    [Fact]
    public void StatusEvent_payload_roundtrip_json()
    {
        var msg = new StatusEvent("LOADPORT", "CASSETTE_LOADED", Severity.Info,
            new Dictionary<string, string> { ["slot"] = "25" }, Ts);
        var json = JsonSerializer.Serialize(msg, TwinJson.Options);
        var back = JsonSerializer.Deserialize<StatusEvent>(json, TwinJson.Options);

        Assert.NotNull(back);
        // record 对 Dictionary 成员是引用相等，往返后断言逐字段值相等
        Assert.Equal(msg.PartId, back!.PartId);
        Assert.Equal(msg.EventCode, back.EventCode);
        Assert.Equal(Severity.Info, back.Severity);
        Assert.Equal("25", back.Payload["slot"]);
        Assert.Equal(Ts, back.Timestamp);
    }

    [Fact]
    public void StatusEvent_null_payload_defaults_to_empty()
    {
        var msg = new StatusEvent("TRACK", "WAFER_PRESENT", Severity.Warning, null!, Ts);
        Assert.Empty(msg.Payload);
    }

    [Fact]
    public void OpticsEvent_roundtrip_json()
    {
        var msg = new OpticsEvent("LIGHT_RING", OpticsKind.LightChannel, 3, 80.0, Ts);
        var json = JsonSerializer.Serialize(msg, TwinJson.Options);
        var back = JsonSerializer.Deserialize<OpticsEvent>(json, TwinJson.Options);

        Assert.NotNull(back);
        Assert.Equal(msg, back);
        Assert.Equal(OpticsKind.LightChannel, back!.Kind);
        Assert.Equal(3, back.Channel);
    }

    [Fact]
    public void WaferResult_roundtrip_json_with_die_map_and_defects()
    {
        var dieMap = new byte[] { 1, 0, 1, 1, 0, 0 };
        var defects = new List<DefectRecord>
        {
            new(1, 10.5, 20.25, 3.4, "Scratch", 128),
            new(2, -5.0, 8.0, 1.2, "Particle", 90),
        };
        var msg = new WaferResult("W25-0001", new WaferMapSnapshot(3, 2, dieMap), defects,
            TimeSpan.FromMilliseconds(42000), Ts);

        var json = JsonSerializer.Serialize(msg, TwinJson.Options);
        Assert.Contains("\"W25-0001\"", json);
        Assert.Contains("AQABAQ", json); // byte[] → Base64

        var back = JsonSerializer.Deserialize<WaferResult>(json, TwinJson.Options);
        Assert.NotNull(back);
        Assert.Equal("W25-0001", back!.WaferId);
        Assert.Equal(2, back.Defects.Count);
        Assert.Equal(3, back.Map.SizeX);
        Assert.Equal(dieMap, back.Map.DieMap);
        Assert.Equal("WAFER_CURRENT", back.PartId); // ITwinMessage 契约
        Assert.Equal(TimeSpan.FromMilliseconds(42000), back.Tact);
    }

    [Fact]
    public void WaferMapSnapshot_die_map_base64_roundtrip()
    {
        var map = new WaferMapSnapshot(2, 2, new byte[] { 255, 0, 128, 64 });
        var json = JsonSerializer.Serialize(map, TwinJson.Options);
        var back = JsonSerializer.Deserialize<WaferMapSnapshot>(json, TwinJson.Options);

        Assert.NotNull(back);
        // record 对 byte[] 成员是引用相等，往返后断言逐字段值相等
        Assert.Equal(2, back!.SizeX);
        Assert.Equal(2, back.SizeY);
        Assert.Equal(map.DieMap, back.DieMap);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void AxisSnapshot_invalid_part_id_throws(string? partId)
    {
        Assert.ThrowsAny<ArgumentException>(() =>
            new AxisSnapshot(partId!, "R", 0, 0, Ts));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    public void AxisSnapshot_invalid_axis_name_throws(string? axisName)
    {
        Assert.ThrowsAny<ArgumentException>(() =>
            new AxisSnapshot(TestPartId, axisName!, 0, 0, Ts));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    public void WaferResult_invalid_wafer_id_throws(string? waferId)
    {
        Assert.ThrowsAny<ArgumentException>(() =>
            new WaferResult(waferId!, new WaferMapSnapshot(1, 1, [1]), [], TimeSpan.Zero, Ts));
    }

    [Fact]
    public void WaferMapSnapshot_invalid_dimensions_throws()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => new WaferMapSnapshot(0, 1, [1]));
        Assert.Throws<ArgumentOutOfRangeException>(() => new WaferMapSnapshot(1, 0, [1]));
        Assert.Throws<ArgumentNullException>(() => new WaferMapSnapshot(1, 1, null!));
    }

    [Fact]
    public void Messages_implement_itwin_message()
    {
        ITwinMessage[] messages =
        [
            new AxisSnapshot(TestPartId, "Z", 0, 0, Ts),
            new StatusEvent("TRACK", "E1", Severity.Info, null!, Ts),
            new OpticsEvent("LIGHT_RING", OpticsKind.LightBrightness, 0, 50, Ts),
            new WaferResult("W1", new WaferMapSnapshot(1, 1, [1]), [], TimeSpan.Zero, Ts),
        ];

        Assert.All(messages, m =>
        {
            Assert.False(string.IsNullOrWhiteSpace(m.PartId));
            Assert.Equal(Ts, m.Timestamp);
        });
    }
}
