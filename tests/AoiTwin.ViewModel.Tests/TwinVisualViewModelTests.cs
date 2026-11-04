using AoiTwin.Core.Bus;
using AoiTwin.ViewModel;
using Xunit;

namespace AoiTwin.ViewModel.Tests;

/// <summary>
/// TA.2 VisualViewModel 层测试（FR-M2-02）：
/// 位姿双缓冲并发无撕裂、光学状态换入、上传队列不丢不重、异常边界与降级标志。
/// </summary>
public class TwinVisualViewModelTests
{
    private static AxisSnapshot Axis(string partId = "ROBOT", string axis = "X", double pos = 1.0) =>
        new(partId, axis, pos, 0, DateTimeOffset.UtcNow);

    [Fact]
    public void Pose_buffer_is_tear_free_under_concurrent_write_read()
    {
        var part = new MotionPartViewModel("ROBOT");
        using var stop = new CancellationTokenSource();

        // 写线程：快照的 16 个矩阵元素全部等于种子值——读到"混合"即撕裂
        var writer = Task.Run(() =>
        {
            var seed = 0L;
            while (!stop.IsCancellationRequested)
            {
                seed++;
                var m = new double[16];
                Array.Fill(m, seed % 1000 + 2.0);
                part.Update(new PoseSnapshot(m, DateTimeOffset.UtcNow));
            }
        });

        try
        {
            // 读线程：每个元素都必须来自同一次快照（写线程种子从 2 起，Identity 初始态的 1/0 混排被跳过）
            for (var i = 0; i < 200_000; i++)
            {
                var matrix = part.ReadLatest().Matrix;
                var first = matrix[0];
                if (first < 2.0)
                {
                    continue; // 尚未收到第一次写入
                }

                Assert.All(matrix, v => Assert.Equal(first, v));
            }
        }
        finally
        {
            stop.Cancel();
            writer.Wait(TimeSpan.FromSeconds(2));
        }
    }

    [Fact]
    public void Wafer_queue_delivers_each_result_exactly_once()
    {
        var queue = new WaferUploadQueue();
        for (var i = 1; i <= 5; i++)
        {
            queue.Enqueue(MakeResult($"W{i}"));
        }

        Assert.Equal(5, queue.Count);

        var taken = new List<string>();
        while (queue.TryTake(out var result))
        {
            taken.Add(result.WaferId);
        }

        Assert.Equal(new[] { "W1", "W2", "W3", "W4", "W5" }, taken); // FIFO 不丢不重
        Assert.Equal(5, queue.TakenTotal);
        Assert.Equal(0, queue.DroppedTotal);
    }

    [Fact]
    public void Wafer_queue_drops_oldest_when_full()
    {
        var queue = new WaferUploadQueue(capacity: 3);
        for (var i = 1; i <= 5; i++)
        {
            queue.Enqueue(MakeResult($"W{i}"));
        }

        Assert.Equal(3, queue.Count);
        Assert.Equal(2, queue.DroppedTotal); // W1、W2 被挤掉

        queue.TryTake(out var first);
        Assert.Equal("W3", first.WaferId); // 最旧的未挤掉部分在前
    }

    [Fact]
    public void Motion_fallback_maps_axis_position_to_translation()
    {
        var hub = new RealtimeHub();
        using var vm = new TwinVisualViewModel(hub, motionPolicyProvider: () => ThrottlePolicy.PassThrough);

        hub.Publish(TwinTopic.Motion, Axis("STAGE", "X", 0.25));
        hub.Publish(TwinTopic.Motion, Axis("STAGE", "Z", -0.1));

        var pose = vm.GetMotionPart("STAGE").ReadLatest();
        Assert.Equal(0.25, pose.Matrix[3]);  // M14
        Assert.Equal(-0.1, pose.Matrix[11]); // M34
    }

    [Fact]
    public void Motion_pose_source_receives_snapshots_and_feeds_refresh()
    {
        var received = new List<AxisSnapshot>();
        var source = new FakePoseSource(received);
        var hub = new RealtimeHub();
        using var vm = new TwinVisualViewModel(hub, source, () => ThrottlePolicy.PassThrough);
        vm.GetMotionPart("ROBOT"); // 预建部件

        hub.Publish(TwinTopic.Motion, Axis("ROBOT", "R", 0.8));
        Assert.Single(received);

        vm.RefreshMotion(DateTimeOffset.UtcNow);
        Assert.Equal(0.8, vm.GetMotionPart("ROBOT").ReadLatest().Matrix[3]);
    }

    [Fact]
    public void Optics_events_update_channel_and_brightness()
    {
        var hub = new RealtimeHub();
        using var vm = new TwinVisualViewModel(hub);
        var now = DateTimeOffset.UtcNow;

        hub.Publish(TwinTopic.Optics, new OpticsEvent("LIGHT_RING", OpticsKind.LightChannel, 3, 0.7, now));
        hub.Publish(TwinTopic.Optics, new OpticsEvent("LIGHT_RING", OpticsKind.LightBrightness, 0, 0.9, now));

        var state = vm.GetOpticsPart("LIGHT_RING").ReadLatest();
        Assert.Equal(0.7, state.ChannelValues[3]);
        Assert.Equal(0.9, state.Brightness);
        Assert.Equal(0, state.ChannelValues[0]); // 未动通道不受影响
    }

    [Fact]
    public void Status_and_result_flow_into_stores()
    {
        var hub = new RealtimeHub();
        using var vm = new TwinVisualViewModel(hub);

        hub.Publish(TwinTopic.Status, new StatusEvent("LOAD_PORT", "CASSette loaded".ToUpper(), Severity.Info, null, DateTimeOffset.UtcNow));
        hub.Publish(TwinTopic.Result, MakeResult("W42"));

        Assert.True(vm.TryGetStatus("LOAD_PORT", out var status));
        Assert.Equal("CASSETTE LOADED", status.EventCode);
        Assert.Equal(1, vm.WaferQueue.Count);
        Assert.Equal("W42", vm.WaferQueue.TryTake(out var r) ? r.WaferId : null);
    }

    [Fact]
    public void Handler_exception_isolated_counted_and_flagged()
    {
        var source = new ThrowingPoseSource();
        var hub = new RealtimeHub();
        using var vm = new TwinVisualViewModel(hub, source, () => ThrottlePolicy.PassThrough);
        var errors = new List<TwinTopic>();
        vm.HandlerError += (topic, _) => errors.Add(topic);
        vm.GetMotionPart("STAGE"); // 渲染装配期预注册部件（与真实时序一致）

        hub.Publish(TwinTopic.Motion, Axis());          // 换算源抛异常 → 被边界捕获
        hub.Publish(TwinTopic.Motion, Axis("STAGE"));   // 后续消息继续处理
        vm.RefreshMotion(DateTimeOffset.UtcNow);        // 换算源对 STAGE 正常出 pose

        Assert.Equal(1, vm.HandlerErrorCount);
        Assert.True(vm.IsDegraded);
        Assert.Equal([TwinTopic.Motion], errors);
        // 正常路径不受影响：STAGE 经换算源换入 1.0 平移
        Assert.Equal(1.0, vm.GetMotionPart("STAGE").ReadLatest().Matrix[3]);
    }

    [Fact]
    public async Task Dispose_stops_all_deliveries()
    {
        var hub = new RealtimeHub(); // RealtimeHub 无 Dispose（M1 契约），生命周期随进程
        var vm = new TwinVisualViewModel(hub, motionPolicyProvider: () => ThrottlePolicy.PassThrough);
        vm.Dispose();

        hub.Publish(TwinTopic.Motion, Axis());
        hub.Publish(TwinTopic.Result, MakeResult("W1"));
        await Task.Delay(100);

        Assert.Equal(0, vm.WaferQueue.Count);
    }

    private static WaferResult MakeResult(string waferId) =>
        new(waferId, new WaferMapSnapshot(4, 4, new byte[16]), [], TimeSpan.FromSeconds(30), DateTimeOffset.UtcNow);

    private sealed class FakePoseSource(List<AxisSnapshot> received) : IMotionPoseSource
    {
        public void OnAxis(AxisSnapshot snapshot) => received.Add(snapshot);

        public PoseSnapshot? GetPose(string partId, DateTimeOffset now) =>
            partId == "ROBOT" ? PoseSnapshot.Translation(0.8, 0, 0, now) : null;
    }

    private sealed class ThrowingPoseSource : IMotionPoseSource
    {
        public void OnAxis(AxisSnapshot snapshot)
        {
            if (snapshot.PartId == "ROBOT")
            {
                throw new InvalidOperationException("换算失败");
            }
        }

        public PoseSnapshot? GetPose(string partId, DateTimeOffset now) =>
            partId == "STAGE" ? PoseSnapshot.Translation(1, 0, 0, now) : null;
    }
}
