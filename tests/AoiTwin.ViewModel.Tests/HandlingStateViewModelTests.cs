using AoiTwin.Core.Bus;
using AoiTwin.ViewModel;
using Xunit;

namespace AoiTwin.ViewModel.Tests;

/// <summary>
/// 装载状态（TA.16，FR-M2-13）：Status Topic 的 Track/LoadPort 负载解析、缺键保旧值、
/// 超容量截断与页头文本。
/// </summary>
public sealed class HandlingStateViewModelTests
{
    [Theory]
    [InlineData("1", true)]
    [InlineData("0", false)]
    [InlineData("true", true)]
    [InlineData("FALSE", false)]
    [InlineData("on", true)]
    public void Track_presence_accepts_plc_style_literals(string raw, bool expected)
    {
        var hub = new RealtimeHub();
        using var state = new HandlingStateViewModel(hub);

        hub.Publish(TwinTopic.Status, Status("TRACK", ("WaferPresent", raw)));

        Assert.Equal(expected, state.WaferOnTrack);
    }

    [Fact]
    public void Unknown_or_malformed_payload_keeps_previous_state()
    {
        var hub = new RealtimeHub();
        using var state = new HandlingStateViewModel(hub);
        hub.Publish(TwinTopic.Status, Status("LOAD_PORT", ("CassetteCount", "12")));
        Assert.Equal(12, state.CassetteCount);

        // 缺键 / 非法值 / 其它部件：一律不改变已有状态（NFR-15 降级语义）
        hub.Publish(TwinTopic.Status, Status("LOAD_PORT", ("CassetteCount", "abc")));
        hub.Publish(TwinTopic.Status, Status("LOAD_PORT"));
        hub.Publish(TwinTopic.Status, Status("ROBOT", ("CassetteCount", "3")));
        hub.Publish(TwinTopic.Status, Status("TRACK", ("WaferPresent", "maybe")));

        Assert.Equal(12, state.CassetteCount);
        Assert.Equal(HandlingStateViewModel.DefaultCassetteCapacity, state.CassetteCapacity);
        Assert.False(state.WaferOnTrack);
        Assert.Equal(1, state.UpdateCount); // 仅第一次有效变化计数
    }

    [Fact]
    public void Cassette_count_clamps_to_capacity_and_raises_changed()
    {
        var hub = new RealtimeHub();
        using var state = new HandlingStateViewModel(hub);
        var changes = 0;
        state.Changed += (_, _) => changes++;

        hub.Publish(TwinTopic.Status, Status("LOAD_PORT", ("CassetteCount", "18")));
        Assert.Equal(18, state.CassetteCount);
        Assert.Equal(1, changes);

        hub.Publish(TwinTopic.Status, Status("LOAD_PORT", ("CassetteCount", "99")));
        Assert.Equal(25, state.CassetteCount); // 截断到 25 槽
        Assert.Equal(2, changes);

        hub.Publish(TwinTopic.Status, Status("LOAD_PORT", ("CassetteCount", "18")));
        Assert.Equal(18, state.CassetteCount);
        Assert.Equal(3, changes);

        // 重复同值：不触发 Changed（驱动器不必白跑一帧）
        hub.Publish(TwinTopic.Status, Status("LOAD_PORT", ("CassetteCount", "18")));
        Assert.Equal(3, changes);
    }

    [Fact]
    public void Caption_reflects_track_and_cassette_state()
    {
        var hub = new RealtimeHub();
        using var state = new HandlingStateViewModel(hub);
        Assert.Equal("Track 空 · Cassette 0/25", state.Caption);

        hub.Publish(TwinTopic.Status, Status("TRACK", ("WaferPresent", "1")));
        hub.Publish(TwinTopic.Status, Status("LOAD_PORT", ("CassetteCount", "7"), ("CassetteCapacity", "13")));

        Assert.Equal("Track 有片 · Cassette 7/13", state.Caption);
        Assert.Equal(13, state.CassetteCapacity);
    }

    [Fact]
    public void Custom_capacity_shrinks_existing_count()
    {
        var hub = new RealtimeHub();
        using var state = new HandlingStateViewModel(hub);
        hub.Publish(TwinTopic.Status, Status("LOAD_PORT", ("CassetteCount", "20")));
        Assert.Equal(20, state.CassetteCount);

        // 容量变小：已装片数同步收敛，避免 20/13 这种非法组合
        hub.Publish(TwinTopic.Status, Status("LOAD_PORT", ("CassetteCapacity", "13")));
        Assert.Equal(13, state.CassetteCapacity);
        Assert.Equal(13, state.CassetteCount);
    }

    private static StatusEvent Status(string partId, params (string Key, string Value)[] payload) =>
        new(
            partId,
            "STATE",
            Severity.Info,
            payload.ToDictionary(p => p.Key, p => p.Value, StringComparer.Ordinal),
            DateTimeOffset.UtcNow);
}
