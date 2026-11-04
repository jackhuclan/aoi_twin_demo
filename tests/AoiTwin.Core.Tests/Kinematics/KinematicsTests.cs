using System.Text;
using AoiTwin.Core.Kinematics;
using Xunit;

namespace AoiTwin.Core.Tests.Kinematics;

/// <summary>
/// TA.3 坐标换算与插值测试（FR-M2-03）：
/// 标定加载与换算、标定缺失恒等降级、插值端点严格相等、时间戳倒退丢弃、环形淘汰、相邻帧位移连续。
/// </summary>
public class KinematicsTests : IDisposable
{
    private readonly string _dir;
    private readonly string _calibrationPath;

    public KinematicsTests()
    {
        _dir = Path.Combine(Path.GetTempPath(), "aoitwin-kin-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_dir);
        _calibrationPath = Path.Combine(_dir, "teach-calibration.json");
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

    // ---- TeachCoordinateMapper ----

    [Fact]
    public void Calibration_maps_scale_sign_offset_to_direction()
    {
        var mapper = new TeachCoordinateMapper(
        [
            new AxisCalibration("STAGE", "X", "X", Scale: 1e-6, Sign: 1, Offset: 0.05),
            new AxisCalibration("STAGE", "Y", "Y", Scale: 1e-6, Sign: -1, Offset: 0),
            new AxisCalibration("ROBOT", "Z", "Z", Scale: 1e-6, Sign: 1, Offset: 0.2),
        ]);

        // 200000 µm → 0.2m + 0.05m 偏置 = 0.25m 沿 X
        var (dx, dy, dz) = mapper.Map("STAGE", "X", 200_000);
        Assert.Equal(0.25, dx, 9);
        Assert.Equal(0, dy);
        Assert.Equal(0, dz);

        // Sign=-1：位置翻向
        var y = mapper.Map("STAGE", "Y", 100_000);
        Assert.Equal(-0.1, y.Dy, 9);

        // 带偏置的 Z
        var z = mapper.Map("ROBOT", "Z", 50_000);
        Assert.Equal(0.25, z.Dz, 9);
    }

    [Fact]
    public void Missing_calibration_falls_back_to_identity()
    {
        var mapper = new TeachCoordinateMapper(
        [
            new AxisCalibration("STAGE", "X", "X", 1e-6, 1, 0),
        ]);

        Assert.True(mapper.Has("STAGE", "X"));
        Assert.False(mapper.Has("ROBOT", "R"));

        // 未标定轴：恒等映射（默认方向表 + 位置原样）
        var r = mapper.Map("ROBOT", "R", 0.4);
        Assert.Equal(0.4, r.Dx, 9);

        var z = mapper.Map("ROBOT", "Z", 0.3);
        Assert.Equal(0.3, z.Dz, 9);
    }

    [Fact]
    public void Missing_calibration_file_loads_empty_table()
    {
        // 文件不存在：不抛，恒等降级
        var mapper = TeachCoordinateMapper.Load(Path.Combine(_dir, "not-exist.json"));
        Assert.False(mapper.Has("STAGE", "X"));
        Assert.Equal(0.5, mapper.Map("STAGE", "X", 0.5).Dx, 9);
    }

    [Fact]
    public void Calibration_file_roundtrip()
    {
        File.WriteAllText(_calibrationPath,
            /* lang=json */ """
            [
              { "PartId": "STAGE", "AxisName": "X", "Direction": "X", "Scale": 0.000001, "Sign": 1, "Offset": 0.05 },
              { "PartId": "STAGE", "AxisName": "THETA", "Direction": "X", "Scale": 0.000001, "Sign": -1, "Offset": 0 }
            ]
            """,
            Encoding.UTF8);

        var mapper = TeachCoordinateMapper.Load(_calibrationPath);
        Assert.Equal(0.25, mapper.Map("STAGE", "X", 200_000).Dx, 9);
        Assert.Equal(-0.02, mapper.Map("STAGE", "THETA", 20_000).Dx, 9);
    }

    // ---- AxisInterpolator ----

    private static DateTimeOffset T(double seconds) =>
        DateTimeOffset.UnixEpoch.AddSeconds(seconds);

    [Fact]
    public void Interpolation_hits_sample_times_exactly()
    {
        var axis = new AxisInterpolator();
        axis.AddSample(T(1), 10);
        axis.AddSample(T(2), 20);
        axis.AddSample(T(3), 40);

        // 端点严格相等：命中样本时刻返回精确值
        Assert.Equal(10, axis.Sample(T(1)));
        Assert.Equal(20, axis.Sample(T(2)));
        Assert.Equal(40, axis.Sample(T(3)));

        // 区间线性：t=2.5 → 20 与 40 的中点
        Assert.Equal(30, axis.Sample(T(2.5)));
    }

    [Fact]
    public void Interpolation_holds_outside_sample_range()
    {
        var axis = new AxisInterpolator();
        axis.AddSample(T(2), 10);
        axis.AddSample(T(4), 20);

        Assert.Equal(10, axis.Sample(T(1)));  // 首样本前 hold
        Assert.Equal(20, axis.Sample(T(9)));  // 末样本后 hold
        Assert.Equal(20, axis.Sample(T(9.5)));
    }

    [Fact]
    public void Regression_timestamps_are_discarded()
    {
        var axis = new AxisInterpolator();
        axis.AddSample(T(1), 10);
        axis.AddSample(T(2), 20);
        axis.AddSample(T(1.5), 99); // 时间戳倒退：丢弃
        axis.AddSample(T(2), 88);   // 重复时间戳：丢弃

        Assert.Equal(2, axis.SampleCount);
        Assert.Equal(20, axis.Sample(T(2)));
    }

    [Fact]
    public void Ring_buffer_evicts_oldest_samples()
    {
        var axis = new AxisInterpolator(capacity: 4);
        for (var i = 1; i <= 10; i++)
        {
            axis.AddSample(T(i), i);
        }

        Assert.Equal(4, axis.SampleCount);
        // 最旧 6 个被淘汰：t≤7 hold 在首样本（=7）
        Assert.Equal(7, axis.Sample(T(6.5)));
        Assert.Equal(7.5, axis.Sample(T(7.5)));
        Assert.Equal(10, axis.Sample(T(10)));
    }

    [Fact]
    public void Adjacent_frames_move_continuously()
    {
        // 模拟设备 2Hz 快照、渲染 60fps 采样：相邻帧位移不得超过单样本区间的全程
        var axis = new AxisInterpolator();
        axis.AddSample(T(0), 0);
        axis.AddSample(T(0.5), 100); // 单步 100

        var previous = axis.Sample(T(0));
        for (var i = 1; i <= 30; i++)
        {
            var current = axis.Sample(T(0.5 * i / 30.0));
            var delta = Math.Abs(current - previous);
            Assert.True(delta <= 100.0 + 1e-9, $"相邻帧位移 {delta} 超过单样本区间");
            previous = current;
        }
    }

    [Fact]
    public void Empty_interpolator_returns_zero()
    {
        var axis = new AxisInterpolator();
        Assert.Equal(0, axis.Sample(DateTimeOffset.UtcNow));
    }
}
