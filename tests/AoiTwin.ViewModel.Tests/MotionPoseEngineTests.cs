using AoiTwin.Core.Bus;
using AoiTwin.Core.Kinematics;
using AoiTwin.ViewModel;
using Xunit;

namespace AoiTwin.ViewModel.Tests;

/// <summary>
/// TA.4 运动位姿引擎测试（FR-M2-03）：轴快照 → 插值 → 标定换算 → 位姿合成。
/// </summary>
public class MotionPoseEngineTests
{
    private static readonly DateTimeOffset T0 = new(2026, 11, 4, 8, 0, 0, TimeSpan.Zero);

    private static TeachCoordinateMapper DemoMapper() => new(
    [
        new AxisCalibration("STAGE", "X", "X", Scale: 1e-6, Sign: 1, Offset: 0),
        new AxisCalibration("STAGE", "Y", "Y", Scale: 1e-6, Sign: 1, Offset: 0),
        new AxisCalibration("STAGE", "Z", "Z", Scale: 1e-6, Sign: 1, Offset: 0),
        new AxisCalibration("STAGE", "THETA", "NONE", Scale: 1e-6, Sign: 1, Offset: 0),
    ]);

    private static AxisSnapshot Snap(string partId, string axis, double posUm, DateTimeOffset time) =>
        new(partId, axis, posUm, 0, time);

    [Fact]
    public void Unknown_part_returns_null()
    {
        var engine = new MotionPoseEngine(DemoMapper());
        Assert.Null(engine.GetPose("STAGE", DateTimeOffset.UtcNow));
    }

    [Fact]
    public void Axis_position_is_calibrated_to_model_translation()
    {
        var engine = new MotionPoseEngine(DemoMapper());
        engine.OnAxis(Snap("STAGE", "X", 250_000, T0)); // 250000 µm → 0.25 m

        var pose = engine.GetPose("STAGE", T0);
        Assert.NotNull(pose);
        Assert.Equal(0.25, pose!.Matrix[3], 9);
        Assert.Equal(0, pose.Matrix[7]);
        Assert.Equal(0, pose.Matrix[11]);
    }

    [Fact]
    public void Multiple_axes_compose_additively()
    {
        var engine = new MotionPoseEngine(DemoMapper());
        engine.OnAxis(Snap("STAGE", "X", 100_000, T0));
        engine.OnAxis(Snap("STAGE", "Y", 50_000, T0));
        engine.OnAxis(Snap("STAGE", "Z", 8_000, T0));

        var pose = engine.GetPose("STAGE", T0);
        Assert.Equal(0.1, pose!.Matrix[3], 9);
        Assert.Equal(0.05, pose.Matrix[7], 9);
        Assert.Equal(0.008, pose.Matrix[11], 9);
    }

    [Fact]
    public void Rotation_axis_with_none_direction_yields_zero_displacement()
    {
        var engine = new MotionPoseEngine(DemoMapper());
        engine.OnAxis(Snap("STAGE", "THETA", 20_000, T0));

        var pose = engine.GetPose("STAGE", T0);
        Assert.NotNull(pose); // 部件有数据：位姿存在
        Assert.Equal(0, pose!.Matrix[3]); // NONE 方向：零位移（旋转可视化待真实轴心资产）
    }

    [Fact]
    public void Regression_timestamp_does_not_move_pose_backwards()
    {
        var engine = new MotionPoseEngine(DemoMapper());
        engine.OnAxis(Snap("STAGE", "X", 250_000, T0));
        engine.OnAxis(Snap("STAGE", "X", 999_999, T0.AddSeconds(-5))); // 倒退样本：被插值器丢弃

        var pose = engine.GetPose("STAGE", T0);
        Assert.Equal(0.25, pose!.Matrix[3], 9);
    }

    [Fact]
    public void Hold_segment_reuses_cached_snapshot()
    {
        var engine = new MotionPoseEngine(DemoMapper());
        engine.OnAxis(Snap("STAGE", "X", 250_000, T0));

        var first = engine.GetPose("STAGE", T0.AddMinutes(1)); // 末样本之后：hold
        var second = engine.GetPose("STAGE", T0.AddMinutes(2));

        Assert.Same(first, second); // 保持段复用缓存，零分配
    }

    [Fact]
    public void Interpolation_advances_between_samples()
    {
        var engine = new MotionPoseEngine(DemoMapper());
        engine.OnAxis(Snap("STAGE", "X", 100_000, T0));
        engine.OnAxis(Snap("STAGE", "X", 200_000, T0.AddSeconds(1)));

        var mid = engine.GetPose("STAGE", T0.AddMilliseconds(500));
        Assert.Equal(0.15, mid!.Matrix[3], 9); // 100→200µm 中点 = 0.15 m

        // 进入 hold 段后复用缓存
        var held = engine.GetPose("STAGE", T0.AddSeconds(5));
        Assert.Equal(0.2, held!.Matrix[3], 9);
    }

    [Fact]
    public void Missing_calibration_degrades_to_identity()
    {
        var engine = new MotionPoseEngine(); // 无标定表
        engine.OnAxis(Snap("STAGE", "X", 0.4, T0)); // 位置按米原样映射

        var pose = engine.GetPose("STAGE", T0);
        Assert.Equal(0.4, pose!.Matrix[3], 9);
    }
}
