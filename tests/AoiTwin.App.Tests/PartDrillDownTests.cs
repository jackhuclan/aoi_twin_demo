using System.Diagnostics;
using AoiTwin.App.Services;
using AoiTwin.App.ViewModels;
using AoiTwin.Core.Bus;
using AoiTwin.ViewModel;
using Xunit;

namespace AoiTwin.App.Tests;

public sealed class PartDrillDownTests
{
    [Fact]
    public void Open_close_leaves_no_subscription_leak()
    {
        var hub = new RealtimeHub();
        var visual = new TwinVisualViewModel(hub, motionPolicyProvider: () => ThrottlePolicy.PassThrough);
        var target = new DrillDownViewModel();
        using var service = new PartDrillDownService(hub, visual, target);

        // 基线：visual 自身订阅 4 个 Topic；service.Open 增 1，Close 释放回基线（无泄漏判据）
        service.Open("ROBOT");
        Assert.Equal("ROBOT", service.OpenedPartId);
        service.Close();
        Assert.Null(service.OpenedPartId);
        Assert.Null(target.PartId);
        Assert.Equal("部件详情（未选中）", target.Title);

        // 关闭后再发状态：面板无变化（订阅确已释放）
        hub.Publish(TwinTopic.Status, new StatusEvent(
            "ROBOT", "E-404", Severity.Alarm,
            new Dictionary<string, string> { ["IO_X"] = "1" }, DateTimeOffset.UtcNow));
        Assert.Empty(target.Alarms);
        Assert.Empty(target.IoLines);
    }

    [Fact]
    public void Open_populates_snapshot_and_increment_within_200ms()
    {
        var hub = new RealtimeHub();
        var visual = new TwinVisualViewModel(hub, motionPolicyProvider: () => ThrottlePolicy.PassThrough);
        var target = new DrillDownViewModel();
        using var service = new PartDrillDownService(
            hub, visual, target,
            new Dictionary<string, string> { ["ROBOT"] = "四轴机械手" });

        // 预置聚合态：状态快照 + 光学快照
        var now = DateTimeOffset.UtcNow;
        hub.Publish(TwinTopic.Status, new StatusEvent(
            "ROBOT", "RUN", Severity.Info,
            new Dictionary<string, string> { ["LifeCount"] = "123456", ["IO_X"] = "1", ["IO_Z"] = "0" }, now));

        var stopwatch = Stopwatch.StartNew();
        service.Open("ROBOT");
        stopwatch.Stop();

        // 判据：切换 ≤ 200ms（同步订阅 + 快照填充，无 IO）
        Assert.True(stopwatch.Elapsed <= TimeSpan.FromMilliseconds(200), $"Open 耗时 {stopwatch.Elapsed.TotalMilliseconds}ms");

        Assert.Equal("部件详情 · 四轴机械手", target.Title);
        Assert.Equal("123456", target.LifeCount);
        Assert.Equal(["IO_X = 1", "IO_Z = 0"], target.IoLines.OrderBy(x => x));
        Assert.NotEqual("—", target.LastStatusAt);

        // 增量：新状态进来面板更新；报警累积（新→旧，上限裁剪）
        hub.Publish(TwinTopic.Status, new StatusEvent(
            "ROBOT", "E-404", Severity.Alarm,
            new Dictionary<string, string> { ["LifeCount"] = "123457" }, now.AddSeconds(1)));
        Assert.Single(target.Alarms);
        Assert.Contains("E-404", target.Alarms[0]);

        // 其他部件的状态不进本面板（过滤）
        hub.Publish(TwinTopic.Status, new StatusEvent(
            "STAGE", "E-500", Severity.Alarm, null, now.AddSeconds(2)));
        Assert.Single(target.Alarms);
    }

    [Fact]
    public void Alarm_history_is_capped()
    {
        var hub = new RealtimeHub();
        var visual = new TwinVisualViewModel(hub, motionPolicyProvider: () => ThrottlePolicy.PassThrough);
        var target = new DrillDownViewModel();
        using var service = new PartDrillDownService(hub, visual, target);
        service.Open("ROBOT");

        var now = DateTimeOffset.UtcNow;
        for (var i = 0; i < PartDrillDownService.MaxAlarmHistory + 10; i++)
        {
            hub.Publish(TwinTopic.Status, new StatusEvent(
                "ROBOT", $"E-{i:D3}", Severity.Alarm, null, now.AddMilliseconds(i)));
        }

        Assert.Equal(PartDrillDownService.MaxAlarmHistory, target.Alarms.Count);
        Assert.Contains("E-029", target.Alarms[0]); // 最新在前
        Assert.DoesNotContain(target.Alarms, line => line.Contains("E-000")); // 最旧被裁剪
    }
}
